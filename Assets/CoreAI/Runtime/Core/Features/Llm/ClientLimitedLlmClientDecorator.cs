using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Локальные клиентские ограничения перед обращением к внутреннему LLM-клиенту: потолок запросов на
    /// сессию и потолок размера промпта.
    /// <para>
    /// Оба отказа — <see cref="LlmErrorCode.ClientLimitExceeded"/>: это решение САМОГО клиента, бэкенд не
    /// спрашивали. Раньше отдавался <see cref="LlmErrorCode.QuotaExceeded"/>, и презентация говорила игроку
    /// «квота аккаунта исчерпана», хотя кончился лишь локальный счётчик сессии.
    /// </para>
    /// <para>
    /// Слот сессии резервируется на время запроса и ВОЗВРАЩАЕТСЯ, если запрос не удался (результат с
    /// ошибкой, терминальный чанк с ошибкой, исключение, отмена): неуспешная попытка лимит не съедает.
    /// Резерв, а не подсчёт постфактум, — чтобы параллельные запросы не проскочили потолок вдвоём.
    /// Поток, который потребитель бросил после части ответа, слот удерживает: бэкенд работу выполнил.
    /// Счётчик живёт столько же, сколько экземпляр, — это и есть «сессия».
    /// </para>
    /// </summary>
    public sealed class ClientLimitedLlmClientDecorator : ILlmClient
    {
        /// <summary>Текст отказа при исчерпании потолка запросов сессии (диагностика, не текст для игрока).</summary>
        public const string RequestLimitError = "ClientLimited request limit exceeded";

        /// <summary>Текст отказа при превышении потолка размера промпта (диагностика, не текст для игрока).</summary>
        public const string PromptLimitError = "ClientLimited prompt character limit exceeded";

        /// <summary>Exception.Data key containing a secondary stream cleanup exception when the request already failed.</summary>
        public const string StreamDisposeExceptionDataKey = "CoreAI.StreamDisposeException";

        private readonly ILlmClient _inner;
        private readonly int _maxRequestsPerSession;
        private readonly int _maxPromptChars;
        private int _reservedRequests;

        /// <summary>
        /// Создаёт локальный клиентский ограничитель для одного разрешённого LLM-клиента.
        /// </summary>
        public ClientLimitedLlmClientDecorator(ILlmClient inner, int maxRequestsPerSession, int maxPromptChars)
        {
            _inner = inner ?? new StubLlmClient();
            _maxRequestsPerSession = maxRequestsPerSession < 0 ? 0 : maxRequestsPerSession;
            _maxPromptChars = maxPromptChars < 0 ? 0 : maxPromptChars;
        }

        /// <summary>
        /// Обёрнутый клиент, к которому уходят запросы, прошедшие локальные ограничения.
        /// </summary>
        public ILlmClient Inner => _inner;

        /// <inheritdoc />
        public bool SupportsNativeToolCalling => _inner?.SupportsNativeToolCalling == true;

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return _inner?.SupportsNativeToolCallingForRole(agentRoleId) == true;
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId, string routingProfileId)
        {
            return _inner?.SupportsNativeToolCallingForRole(agentRoleId, routingProfileId) == true;
        }

        /// <inheritdoc />
        public int? ResolveContextWindowTokensForRole(string agentRoleId, string routingProfileId)
        {
            return _inner?.ResolveContextWindowTokensForRole(agentRoleId, routingProfileId);
        }

        /// <inheritdoc />
        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _inner?.SetTools(tools);
        }

        /// <summary>
        /// Проверяет локальные ограничения и делегирует нестриминговый запрос.
        /// </summary>
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            string rejection = TryReserve(request);
            if (rejection != null)
            {
                return Rejected(rejection);
            }

            bool succeeded = false;
            try
            {
                LlmCompletionResult result = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                succeeded = result != null && result.Ok;
                return result;
            }
            finally
            {
                if (!succeeded)
                {
                    ReleaseReservation();
                }
            }
        }

        /// <summary>
        /// Проверяет локальные ограничения и делегирует стриминговый запрос.
        /// </summary>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            string rejection = TryReserve(request);
            if (rejection != null)
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = rejection,
                    ErrorCode = LlmErrorCode.ClientLimitExceeded
                };
                yield break;
            }

            bool failed = false;
            bool sawAnyChunk = false;
            Exception primaryFailure = null;
            IAsyncEnumerator<LlmStreamChunk> enumerator = null;
            try
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    enumerator = _inner.CompleteStreamingAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                }
                catch (Exception exception) { failed = true; primaryFailure = exception; throw; }
                while (true)
                {
                    bool hasNext;
                    LlmStreamChunk chunk;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        hasNext = await enumerator.MoveNextAsync();
                        cancellationToken.ThrowIfCancellationRequested();
                        chunk = hasNext ? enumerator.Current : null;
                    }
                    catch (Exception exception) { failed = true; primaryFailure = exception; throw; }
                    if (!hasNext) break;
                    if (chunk != null)
                    {
                        sawAnyChunk = true;
                        if (!string.IsNullOrEmpty(chunk.Error) || chunk.ErrorCode != LlmErrorCode.None) failed = true;
                    }
                    yield return chunk;
                }
            }
            finally
            {
                try
                {
                    if (enumerator != null) await enumerator.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failed = true;
                    // WHY: cleanup diagnostics must not replace the provider failure used by auth/retry classification.
                    if (primaryFailure == null) throw;
                    primaryFailure.Data[StreamDisposeExceptionDataKey] = exception;
                }
                finally
                {
                    // WHY: an actual stream/cleanup failure refunds once; abandoning a partial answer retains the slot.
                    if (failed || !sawAnyChunk || cancellationToken.IsCancellationRequested) ReleaseReservation();
                }
            }
        }

        /// <summary>Причина отказа либо null, если слот зарезервирован (или потолки выключены).</summary>
        private string TryReserve(LlmCompletionRequest request)
        {
            if (_maxPromptChars > 0 && EstimatePromptChars(request) > _maxPromptChars)
            {
                return PromptLimitError;
            }

            if (_maxRequestsPerSession <= 0)
            {
                return null;
            }

            int reserved = Interlocked.Increment(ref _reservedRequests);
            if (reserved > _maxRequestsPerSession)
            {
                Interlocked.Decrement(ref _reservedRequests);
                return RequestLimitError;
            }

            return null;
        }

        private void ReleaseReservation()
        {
            if (_maxRequestsPerSession > 0)
            {
                Interlocked.Decrement(ref _reservedRequests);
            }
        }

        private static LlmCompletionResult Rejected(string error)
        {
            return new LlmCompletionResult
            {
                Ok = false,
                Error = error,
                ErrorCode = LlmErrorCode.ClientLimitExceeded
            };
        }

        private static int EstimatePromptChars(LlmCompletionRequest request)
        {
            if (request == null)
            {
                return 0;
            }

            int total = (request.SystemPrompt?.Length ?? 0) + (request.UserPayload?.Length ?? 0);
            if (request.ChatHistory == null)
            {
                return total;
            }

            foreach (Microsoft.Extensions.AI.ChatMessage message in request.ChatHistory)
            {
                total += message.Text?.Length ?? 0;
            }

            return total;
        }
    }
}
