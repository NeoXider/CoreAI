#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Предохранитель для <see cref="ILlmClient"/>. После <c>failureThreshold</c> ТРАНЗИЕНТНЫХ сбоев подряд
    /// (см. <see cref="IsTransientFailure"/>) предохранитель <b>размыкается</b> и коротит последующие вызовы
    /// результатом <see cref="LlmErrorCode.BackendUnavailable"/> <i>без обращения к внутреннему клиенту</i> —
    /// мёртвый основной бэкенд больше не стоит <c>таймаут × (retries + 1)</c> на каждый ход. Через
    /// <c>openDurationMs</c> предохранитель переходит в <b>полуоткрытое</b> состояние и пропускает ровно
    /// один пробный запрос: успех — <b>замыкается</b>, сбой — снова размыкается на период охлаждения.
    /// <para>
    /// Считаются только ТРАНЗИЕНТНЫЕ сбои (таймаут, rate limit, недоступность бэкенда, общая ошибка
    /// провайдера, ошибка маршрутизации) — и неважно, пришли они результатом, терминальным чанком или
    /// брошенным <see cref="LlmClientException"/>. Сбои по вине вызывающего — истёкшая авторизация,
    /// требуется оплата, неверный запрос, превышение контекста, отмена — предохранитель НИКОГДА не
    /// размыкают: повтор в другой момент не поможет. Такие исключения перебрасываются как есть, с типом,
    /// кодом и HTTP-статусом, чтобы внешние декораторы (retry/fallback) видели ту же классификацию, что
    /// и без предохранителя. Поток, кончившийся без единого чанка, — сбой: бэкенд не ответил ничего.
    /// </para>
    /// <para>
    /// Время подаётся как монотонный источник миллисекунд, поэтому предохранитель полностью
    /// детерминирован в тестах.
    /// </para>
    /// </summary>
    public sealed class CircuitBreakerLlmClientDecorator : ILlmClient
    {
        private enum State
        {
            Closed,
            Open,
            HalfOpen
        }

        private readonly ILlmClient _inner;
        private readonly int _failureThreshold;
        private readonly long _openDurationMs;
        private readonly Func<long> _nowMs;
        private readonly Action<string> _log;
        private readonly object _gate = new();

        private State _state = State.Closed;
        private int _consecutiveFailures;
        private long _openedAtMs;
        private bool _halfOpenProbeInFlight;
        private long _generation;

        /// <param name="inner">Защищаемый клиент.</param>
        /// <param name="failureThreshold">Сколько транзиентных сбоев подряд размыкают предохранитель (мин. 1).</param>
        /// <param name="openDurationMs">Сколько предохранитель остаётся разомкнутым до пробного запроса (мин. 1).</param>
        /// <param name="nowMs">Монотонные часы в миллисекундах (инъекция ради детерминированных тестов).</param>
        /// <param name="log">Необязательный однострочный приёмник диагностики переходов состояния.</param>
        public CircuitBreakerLlmClientDecorator(
            ILlmClient inner,
            int failureThreshold,
            long openDurationMs,
            Func<long> nowMs,
            Action<string> log = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _failureThreshold = failureThreshold < 1 ? 1 : failureThreshold;
            _openDurationMs = openDurationMs < 1 ? 1 : openDurationMs;
            _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
            _log = log;
        }

        public bool SupportsNativeToolCalling => _inner.SupportsNativeToolCalling;

        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return _inner.SupportsNativeToolCallingForRole(agentRoleId);
        }

        public bool SupportsNativeToolCallingForRole(string agentRoleId, string routingProfileId)
        {
            return _inner.SupportsNativeToolCallingForRole(agentRoleId, routingProfileId);
        }

        public int? ResolveContextWindowTokensForRole(string agentRoleId, string routingProfileId)
        {
            return _inner.ResolveContextWindowTokensForRole(agentRoleId, routingProfileId);
        }

        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _inner.SetTools(tools);
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnter(out long lease, out string rejectReason))
            {
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = rejectReason,
                    ErrorCode = LlmErrorCode.BackendUnavailable
                };
            }

            bool classified = false;
            try
            {
                LlmCompletionResult result;
                try
                {
                    result = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (LlmOperationTimeoutException) when (!cancellationToken.IsCancellationRequested)
                {
                    RecordFailure(lease);
                    classified = true;
                    throw;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // ПОЧЕМУ: отмена — намерение вызывающего, а не сбой бэкенда: не считать и не глотать.
                    throw;
                }
                catch (LlmClientException ex)
                {
                    // ПОЧЕМУ: типизированное исключение уже несёт классификацию адаптера. Судить по ней —
                    // три 401 подряд не размыкают предохранитель — и перебрасывать КАК ЕСТЬ: обёртка в
                    // ProviderError без статуса делала 402 ретраебельным для внешних декораторов.
                    RecordResult(lease, false, ex.ErrorCode);
                    classified = true;
                    throw;
                }
                catch (Exception)
                {
                    // Нетипизированный бросок внутреннего клиента — транзиентный сбой бэкенда; сам объект
                    // исключения перебрасывается без изменений.
                    RecordFailure(lease);
                    classified = true;
                    throw;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    RecordResult(lease, result?.Ok ?? false, result?.ErrorCode ?? LlmErrorCode.ProviderError);
                    classified = true;
                }
                return result;
            }
            finally
            {
                if (!classified)
                {
                    // ПОЧЕМУ: вызов закончился без вердикта о здоровье (отмена): освободить слот пробного
                    // запроса, чтобы предохранитель не завис в ожидании результата, который не придёт.
                    ReleaseHalfOpenProbe(lease);
                }
            }
        }

        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnter(out long lease, out string rejectReason))
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = rejectReason,
                    ErrorCode = LlmErrorCode.BackendUnavailable
                };
                yield break;
            }

            bool sawTerminalFailure = false;
            LlmErrorCode terminalCode = LlmErrorCode.None;
            bool sawAnyChunk = false;
            bool streamEnded = false;
            bool classified = false;

            IAsyncEnumerator<LlmStreamChunk> e = null;
            try
            {
                // ПОЧЕМУ: получаем внутри try, чтобы синхронный бросок внутреннего клиента всё равно прошёл
                // через finally, освобождающий слот пробного запроса, а не заклинил предохранитель.
                e = _inner.CompleteStreamingAsync(request, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

                while (true)
                {
                    LlmStreamChunk chunk;
                    // ПОЧЕМУ: C# запрещает `yield` внутри catch, поэтому сбой внутреннего потока
                    // запоминается здесь, а терминальный чанк выдаётся ПОСЛЕ try/catch.
                    LlmStreamChunk faultChunk = null;
                    try
                    {
                        if (!await e.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        chunk = e.Current;
                    }
                    catch (LlmOperationTimeoutException) when (!cancellationToken.IsCancellationRequested)
                    {
                        RecordFailure(lease);
                        classified = true;
                        faultChunk = new LlmStreamChunk
                        {
                            IsDone = true, Error = "LLM request timed out.", ErrorCode = LlmErrorCode.Timeout
                        };
                        chunk = null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (LlmClientException ex)
                    {
                        // Классификация адаптера сохраняется в чанке целиком (код, статус, retry-after):
                        // сбой по вине вызывающего не размыкает предохранитель и не становится ProviderError.
                        RecordResult(lease, false, ex.ErrorCode);
                        classified = true;
                        faultChunk = new LlmStreamChunk
                        {
                            IsDone = true,
                            Error = ex.Message,
                            ErrorCode = ex.ErrorCode,
                            HttpStatus = ex.HttpStatus,
                            RetryAfterSeconds = ex.RetryAfterSeconds
                        };
                        chunk = null;
                    }
                    catch (Exception ex)
                    {
                        RecordFailure(lease);
                        classified = true;
                        faultChunk = new LlmStreamChunk
                        {
                            IsDone = true,
                            Error = ex.Message,
                            ErrorCode = LlmErrorCode.ProviderError
                        };
                        chunk = null;
                    }

                    if (faultChunk != null)
                    {
                        yield return faultChunk;
                        yield break;
                    }

                    sawAnyChunk |= chunk != null;
                    if (chunk != null && (!string.IsNullOrEmpty(chunk.Error) || chunk.ErrorCode != LlmErrorCode.None))
                    {
                        sawTerminalFailure = true;
                        terminalCode = chunk.ErrorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : chunk.ErrorCode;
                    }

                    yield return chunk;
                }

                streamEnded = true;
            }
            finally
            {
                try
                {
                    if (e != null)
                    {
                        await e.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    // Cleanup may throw, but every admitted lease still receives a verdict or releases
                    // its probe slot. Abandonment without a backend error is not a health failure.
                    if (!classified)
                    {
                        if (cancellationToken.IsCancellationRequested &&
                            (!sawTerminalFailure || terminalCode == LlmErrorCode.Cancelled))
                        {
                            ReleaseHalfOpenProbe(lease);
                        }
                        else if (sawTerminalFailure)
                        {
                            RecordResult(lease, false, terminalCode);
                        }
                        else if (streamEnded && !sawAnyChunk)
                        {
                            RecordFailure(lease);
                        }
                        else if (streamEnded)
                        {
                            RecordSuccess(lease);
                        }
                        else
                        {
                            ReleaseHalfOpenProbe(lease);
                        }
                    }
                }
            }
        }

        /// <summary>Имя текущего состояния для диагностики/тестов: "Closed", "Open" или "HalfOpen".</summary>
        public string StateName
        {
            get
            {
                lock (_gate)
                {
                    return _state.ToString();
                }
            }
        }

        /// <summary>
        /// Решает, можно ли пропустить вызов. Переводит Open→HalfOpen по истечении охлаждения и
        /// допускает ровно один пробный запрос. Возвращает false (с причиной), пока предохранитель разомкнут.
        /// </summary>
        private bool TryEnter(out long lease, out string rejectReason)
        {
            lock (_gate)
            {
                lease = _generation;
                if (_state == State.Open)
                {
                    if (_nowMs() - _openedAtMs >= _openDurationMs)
                    {
                        _state = State.HalfOpen;
                        lease = ++_generation;
                        _halfOpenProbeInFlight = true;
                        _log?.Invoke("[CircuitBreaker] half-open: admitting one probe request.");
                        rejectReason = null;
                        return true;
                    }

                    rejectReason =
                        "Circuit breaker open: backend is failing; short-circuited to avoid repeated " +
                        "timeouts. It will retry automatically after a cooldown.";
                    return false;
                }

                if (_state == State.HalfOpen)
                {
                    // ПОЧЕМУ: в полуоткрытом состоянии в полёте может быть ровно ОДИН пробный запрос. Пропуск
                    // всех одновременных вызывающих вываливал весь бэклог на бэкенд, который скорее всего ещё лежит.
                    if (_halfOpenProbeInFlight)
                    {
                        rejectReason =
                            "Circuit breaker half-open: a single probe request is already in flight; " +
                            "short-circuited until the probe result is known.";
                        return false;
                    }

                    _halfOpenProbeInFlight = true;
                    lease = ++_generation;
                    _log?.Invoke("[CircuitBreaker] half-open: admitting one probe request.");
                    rejectReason = null;
                    return true;
                }

                rejectReason = null;
                return true;
            }
        }

        /// <summary>
        /// Освобождает слот пробного запроса, когда вызов закончился без вердикта успех/сбой (потребитель
        /// отменил вызов или бросил поток). Предохранитель остаётся полуоткрытым, следующий вызов
        /// становится новым пробным; брошенный поток никогда не засчитывается как сбой бэкенда.
        /// </summary>
        private void ReleaseHalfOpenProbe(long lease)
        {
            lock (_gate)
            {
                if (lease == _generation && _state == State.HalfOpen)
                {
                    _halfOpenProbeInFlight = false;
                }
            }
        }

        private void RecordResult(long lease, bool ok, LlmErrorCode code)
        {
            if (ok)
            {
                RecordSuccess(lease);
                return;
            }

            if (code == LlmErrorCode.None || IsTransientFailure(code))
            {
                RecordFailure(lease);
            }
            else
            {
                // ПОЧЕМУ: сбой по вине вызывающего (авторизация, оплата, неверный запрос, контекст, пустой
                // ответ) — не проблема здоровья бэкенда: предохранитель не размыкать; а пробный запрос,
                // вернувший такой результат, всё же означает, что бэкенд достижим, — для состояния это
                // мягкий успех.
                RecordSuccess(lease);
            }
        }

        private void RecordSuccess(long lease)
        {
            lock (_gate)
            {
                if (lease != _generation)
                {
                    return;
                }
                _consecutiveFailures = 0;
                _halfOpenProbeInFlight = false;
                if (_state != State.Closed)
                {
                    _state = State.Closed;
                    _generation++;
                    _log?.Invoke("[CircuitBreaker] closed: backend recovered.");
                }
            }
        }

        private void RecordFailure(long lease)
        {
            lock (_gate)
            {
                if (lease != _generation)
                {
                    return;
                }
                _halfOpenProbeInFlight = false;
                if (_state == State.HalfOpen)
                {
                    _state = State.Open;
                    _generation++;
                    _openedAtMs = _nowMs();
                    _log?.Invoke("[CircuitBreaker] re-opened: half-open probe failed.");
                    return;
                }

                _consecutiveFailures++;
                if (_state == State.Closed && _consecutiveFailures >= _failureThreshold)
                {
                    _state = State.Open;
                    _generation++;
                    _openedAtMs = _nowMs();
                    _log?.Invoke(
                        $"[CircuitBreaker] opened after {_consecutiveFailures} consecutive transient failures.");
                }
            }
        }

        /// <summary>
        /// Транзиентные сбои, ради которых стоит размыкаться: бэкенд (временно) нездоров, и долбить его —
        /// лишь тратить таймаут на каждый вызов. Коды по вине вызывающего исключены: повтор не поможет.
        /// </summary>
        private static bool IsTransientFailure(LlmErrorCode code)
        {
            switch (code)
            {
                case LlmErrorCode.Timeout:
                case LlmErrorCode.RateLimited:
                case LlmErrorCode.BackendUnavailable:
                case LlmErrorCode.ProviderError:
                case LlmErrorCode.RoutingError:
                    return true;
                default:
                    return false;
            }
        }
    }
}
#endif
