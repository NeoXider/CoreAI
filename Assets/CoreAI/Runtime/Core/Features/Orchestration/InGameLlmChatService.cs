using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// Чат-сервис, который держит историю разговора игрока вокруг запросов к LLM.
    /// <para>
    /// Отказы всегда типизированы (<see cref="LlmCompletionResult.ErrorCode"/>): пустое сообщение —
    /// <see cref="LlmErrorCode.InvalidRequest"/>, скользящее окно лимита — <see cref="LlmErrorCode.RateLimited"/>
    /// с <see cref="LlmCompletionResult.RetryAfterSeconds"/>, пустой ответ модели —
    /// <see cref="LlmErrorCode.EmptyResponse"/>, истёкший таймаут — <see cref="LlmErrorCode.Timeout"/>.
    /// Потребитель без кода не мог отличить отказ от ответа модели и показывал текст отказа как реплику.
    /// </para>
    /// <para>
    /// Слот скользящего окна занимается только на время попытки и ВОЗВРАЩАЕТСЯ, если попытка не удалась
    /// (ошибка, пустой ответ, таймаут, отмена): после сбоя бэкенда игрок не «добирает» лимит ошибками.
    /// Резервируется он уже ПОСЛЕ ожидания гейта — заявка, снятая в очереди и отменённая до старта,
    /// раньше сгорала впустую; быстрый отказ без ожидания даёт предварительная проверка окна.
    /// </para>
    /// <para>
    /// Таймаут — по желанию хоста: с <c>requestTimeoutSecondsProvider</c> клиент оборачивается в
    /// <see cref="TimeoutLlmClientDecorator"/>, и зависший запрос не держит гейт бесконечно. В Unity-конвейере
    /// клиент уже ограничен внешним декоратором, и провайдер передавать НЕ нужно — иначе будет два одинаковых
    /// таймера; провайдер нужен портативному хосту, который подаёт сюда собственный <see cref="ILlmClient"/>.
    /// </para>
    /// </summary>
    public sealed class InGameLlmChatService : IInGameLlmChatService
    {
        private readonly ILlmClient _llm;
        private readonly IAgentSystemPromptProvider _systemPrompts;
        private readonly List<(string Role, string Text)> _turns = new();
        private readonly int _maxMessages;
        private readonly object _historyLock = new();
        private readonly object _rateLock = new();

        // ПОЧЕМУ: запросы внахлёст гонялись: второй снимок истории мог не увидеть первый ход, а дописывания
        // перемешивались. Гейт сериализует «снимок → LLM → дописать», и каждый запрос видит все
        // предыдущие завершённые ходы по порядку.
        private readonly SemaphoreSlim _requestGate = new(1, 1);

        private readonly int _maxRequestsPerWindow;
        private readonly TimeSpan _rateLimitWindow;
        private readonly List<DateTime> _acceptedStamps = new();
        private long _totalRejected;

        /// <summary>
        /// Создаёт чат-сервис.
        /// </summary>
        /// <param name="llm">LLM-клиент.</param>
        /// <param name="systemPrompts">Поставщик системных промптов.</param>
        /// <param name="maxMessages">Сколько сообщений (реплик, не пар) хранить в истории.</param>
        /// <param name="maxRequestsPerWindow">Потолок успешных запросов в скользящем окне; 0 — без лимита.</param>
        /// <param name="rateLimitWindowSeconds">Длина скользящего окна в секундах.</param>
        /// <param name="requestTimeoutSecondsProvider">
        /// Таймаут одного запроса в секундах, читается на каждый вызов; null или значение &lt;= 0 — без
        /// собственного таймаута (клиент ограничен снаружи либо не ограничен вовсе).
        /// </param>
        /// <param name="asyncMarshaler">Планировщик задержек хоста для таймаута; null — управляемая задержка.</param>
        public InGameLlmChatService(
            ILlmClient llm,
            IAgentSystemPromptProvider systemPrompts,
            int maxMessages = 24,
            int maxRequestsPerWindow = 10,
            int rateLimitWindowSeconds = 60,
            Func<float> requestTimeoutSecondsProvider = null,
            ILlmAsyncMarshaler asyncMarshaler = null)
        {
            _llm = llm != null && requestTimeoutSecondsProvider != null
                ? new TimeoutLlmClientDecorator(llm, requestTimeoutSecondsProvider, asyncMarshaler)
                : llm;
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
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = "empty message",
                    ErrorCode = LlmErrorCode.InvalidRequest
                };
            }

            // Быстрый отказ: не ждать в очереди за чужим ответом модели, если окно уже полно.
            if (IsRateWindowFull(out int retryAfterSeconds))
            {
                return RateLimited(retryAfterSeconds);
            }

            string baseSystem = _systemPrompts.TryGetSystemPrompt(BuiltInAgentRoleIds.SmartChat, out string sys) &&
                                !string.IsNullOrWhiteSpace(sys)
                ? sys.Trim()
                : "You are a helpful in-game assistant.";

            string prefix = CoreAISettings.UniversalSystemPromptPrefix;
            string system = string.IsNullOrWhiteSpace(prefix)
                ? baseSystem
                : prefix.TrimEnd() + "\n" + baseSystem;

            // ПОЧЕМУ: _historyLock охраняет лишь отдельные чтения/записи (HistoryPairCount / ClearHistory),
            // а не последовательность «снимок → LLM → дописать». _requestGate сериализует её целиком, чтобы
            // параллельный запрос не снял снимок без предыдущего хода и не вклинился дописыванием.
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!TryAcquireRateSlot(out DateTime stamp, out retryAfterSeconds))
                {
                    return RateLimited(retryAfterSeconds);
                }

                bool consumed = false;
                try
                {
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

                    LlmCompletionResult result;
                    try
                    {
                        result = await _llm.CompleteAsync(
                            new LlmCompletionRequest
                            {
                                AgentRoleId = BuiltInAgentRoleIds.SmartChat,
                                SystemPrompt = system,
                                ChatHistory = history,
                                TraceId = Guid.NewGuid().ToString("N")
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (LlmOperationTimeoutException)
                    {
                        // Таймаут библиотеки при живом токене вызывающего: контракт сервиса — результат,
                        // а не исключение. Настоящая отмена вызывающим проходит наружу как есть.
                        result = Failed(LlmErrorCode.Timeout);
                    }

                    if (result == null)
                    {
                        result = Failed(LlmErrorCode.EmptyResponse);
                    }
                    else if (result.Ok && string.IsNullOrEmpty(result.Content))
                    {
                        // ПОЧЕМУ: «успех» без текста для игрока — не ответ. Раньше такой результат уходил
                        // потребителю как Ok, и панель печатала пустую реплику.
                        result = Failed(LlmErrorCode.EmptyResponse);
                    }

                    if (result.Ok)
                    {
                        consumed = true;
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
                finally
                {
                    if (!consumed)
                    {
                        ReleaseRateSlot(stamp);
                    }
                }
            }
            finally
            {
                _requestGate.Release();
            }
        }

        /// <summary>
        /// Снимок состояния ограничителя для диагностики / UI.
        /// </summary>
        public RateLimiterMetrics GetRateLimiterMetrics()
        {
            lock (_rateLock)
            {
                PruneExpired(DateTime.UtcNow);
                return new RateLimiterMetrics(
                    _maxRequestsPerWindow,
                    (int)_rateLimitWindow.TotalSeconds,
                    _acceptedStamps.Count,
                    _totalRejected);
            }
        }

        private static LlmCompletionResult RateLimited(int retryAfterSeconds)
        {
            return new LlmCompletionResult
            {
                Ok = false,
                Error = "rate_limited: too many requests. Please wait before sending another message.",
                ErrorCode = LlmErrorCode.RateLimited,
                RetryAfterSeconds = retryAfterSeconds > 0 ? retryAfterSeconds : null
            };
        }

        private static LlmCompletionResult Failed(LlmErrorCode code)
        {
            return new LlmCompletionResult
            {
                Ok = false,
                Error = LlmErrorPresentation.ForErrorCode(code),
                ErrorCode = code
            };
        }

        /// <summary>Полно ли окно прямо сейчас (без резервирования). Отказ засчитывается в метрики.</summary>
        private bool IsRateWindowFull(out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            if (_maxRequestsPerWindow <= 0)
            {
                return false;
            }

            lock (_rateLock)
            {
                DateTime now = DateTime.UtcNow;
                PruneExpired(now);
                if (_acceptedStamps.Count < _maxRequestsPerWindow)
                {
                    return false;
                }

                _totalRejected++;
                retryAfterSeconds = RetryAfterSeconds(now);
                return true;
            }
        }

        /// <summary>
        /// Пытается занять один слот скользящего окна. Слот помечен временем, чтобы его можно было
        /// вернуть, если попытка не удалась.
        /// </summary>
        private bool TryAcquireRateSlot(out DateTime stamp, out int retryAfterSeconds)
        {
            stamp = default;
            retryAfterSeconds = 0;
            if (_maxRequestsPerWindow <= 0)
            {
                return true;
            }

            lock (_rateLock)
            {
                DateTime now = DateTime.UtcNow;
                PruneExpired(now);
                if (_acceptedStamps.Count >= _maxRequestsPerWindow)
                {
                    _totalRejected++;
                    retryAfterSeconds = RetryAfterSeconds(now);
                    return false;
                }

                stamp = now;
                _acceptedStamps.Add(now);
                return true;
            }
        }

        /// <summary>Возвращает слот неудавшейся попытки: снимается последняя отметка с этим временем.</summary>
        private void ReleaseRateSlot(DateTime stamp)
        {
            if (_maxRequestsPerWindow <= 0)
            {
                return;
            }

            lock (_rateLock)
            {
                for (int i = _acceptedStamps.Count - 1; i >= 0; i--)
                {
                    if (_acceptedStamps[i] == stamp)
                    {
                        _acceptedStamps.RemoveAt(i);
                        return;
                    }
                }
            }
        }

        /// <summary>Вызывать под <see cref="_rateLock"/>.</summary>
        private void PruneExpired(DateTime now)
        {
            DateTime cutoff = now - _rateLimitWindow;
            int expired = 0;
            while (expired < _acceptedStamps.Count && _acceptedStamps[expired] < cutoff)
            {
                expired++;
            }

            if (expired > 0)
            {
                _acceptedStamps.RemoveRange(0, expired);
            }
        }

        /// <summary>Через сколько секунд освободится самый старый слот окна. Вызывать под <see cref="_rateLock"/>.</summary>
        private int RetryAfterSeconds(DateTime now)
        {
            if (_acceptedStamps.Count == 0)
            {
                return 0;
            }

            double seconds = (_acceptedStamps[0] + _rateLimitWindow - now).TotalSeconds;
            return seconds <= 0 ? 1 : (int)Math.Ceiling(seconds);
        }
    }
}
