using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Таймаут запроса для <see cref="ILlmClient"/> в портативном ядре. По истечении
    /// <c>timeoutSeconds</c> (читается на каждый вызов, чтобы горячая смена настроек действовала со
    /// следующего запроса) отменяет СВЯЗАННЫЙ токен, переданный внутреннему клиенту.
    /// <para>
    /// <see cref="CompleteAsync"/> ограничивает общее время одного нестримингового вызова.
    /// <see cref="CompleteStreamingAsync"/> — бюджет ПРОСТОЯ, а не всего хода: стриминговый вызов —
    /// внешняя обёртка целого хода с инструментами (модель → вызовы инструментов → модель → …), и фиксированный
    /// общий бюджет обрезал бы здоровый длинный ход, как только сумма его шагов перевалит за таймаут.
    /// Поэтому каждый выданный чанк отмечает прогресс, и срабатывает только настоящий застой — ни одного
    /// чанка за полное окно.
    /// </para>
    /// <para>
    /// <b>Что декоратор гарантирует и чего не гарантирует.</b> Он отменяет связанный токен и ограничивает
    /// ожидание клиента, даже если внутренний вызов игнорирует отмену. Брошенный
    /// <see cref="OperationCanceledException"/> при живом токене вызывающего становится
    /// <see cref="LlmOperationTimeoutException"/> (нестриминг) или терминальным чанком с
    /// <see cref="LlmErrorCode.Timeout"/> (стриминг); результат/чанк с кодом <see cref="LlmErrorCode.Cancelled"/>,
    /// полученный после срабатывания таймера, переписывается в <see cref="LlmErrorCode.Timeout"/>. Внутренний
    /// клиент, который переданный токен не наблюдает вовсе, может продолжить работу в фоне: его поздний сбой
    /// наблюдается, а освобождение итератора ждёт завершения активного MoveNext, не задерживая вызывающего.
    /// Отмена от самого вызывающего всегда проходит без изменений.
    /// </para>
    /// <para>
    /// Живёт в <c>CoreAI.Core</c>, чтобы headless-хосты, тесты и не-Unity потребители тоже получали таймаут.
    /// Планирование дедлайна отдано <see cref="ILlmAsyncMarshaler.DelayAsync"/>: Unity подставляет задержку на
    /// PlayerLoop, и декоратор работает в WebGL; портативные хосты остаются на обычной управляемой задержке.
    /// Сбой самого таймера (задержка хоста бросила что-то, кроме нашей остановки) — НЕ истечение таймера:
    /// запрос не отменяется и не репортится как таймаут, а сбой передаётся в <c>onDeadlineTimerFault</c>;
    /// дедлайн для этого запроса остаётся неподкреплённым.
    /// </para>
    /// </summary>
    public sealed class TimeoutLlmClientDecorator : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly Func<float> _timeoutSecondsProvider;
        private readonly ILlmAsyncMarshaler _asyncMarshaler;
        private readonly Action<Exception> _onDeadlineTimerFault;

        // One registration per request, reused by every streamed MoveNext. No timer or polling task
        // is allocated for individual chunks.
        private sealed class CancellationSignal : IDisposable
        {
            private readonly TaskCompletionSource<bool> _source =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _registration;

            public CancellationSignal(CancellationToken token)
            {
                _registration = token.Register(() => _source.TrySetResult(true));
            }

            public Task Task => _source.Task;
            public void Dispose() => _registration.Dispose();
        }

        private static async Task<T> AwaitOperationAsync<T>(Task<T> operation, CancellationSignal signal,
            CancellationToken token)
        {
            if (!operation.IsCompleted)
            {
                await Task.WhenAny(operation, signal.Task).ConfigureAwait(false);
                if (!operation.IsCompleted)
                {
                    throw new OperationCanceledException(token);
                }
            }

            return await operation.ConfigureAwait(false);
        }

        private static async Task ObserveOperationAsync(Task operation)
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The caller has already received the deadline/cancellation outcome. Observing
                // this eventual failure prevents an unobserved background task exception.
            }
        }

        private static async Task DisposeAfterOperationAsync(Task operation, IAsyncEnumerator<LlmStreamChunk> enumerator)
        {
            await ObserveOperationAsync(operation).ConfigureAwait(false);
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cleanup belongs to the abandoned operation and must also be observed.
            }
        }

        /// <summary>
        /// Дедлайн простоя на одном сторожевом ожидании. ПОЧЕМУ так: прежняя реализация на каждый чанк
        /// создавала новый источник отмены, отменяла и освобождала предыдущий и запускала новую задачу
        /// ожидания, а отмена предыдущей задачи заставляла ожидающего получить исключение — минимум одно
        /// брошенное и пойманное исключение плюс несколько аллокаций на КАЖДЫЙ токен потока, при 30–60
        /// токенах в секунду под IL2CPP/WebGL это заметная цена. Теперь отметка прогресса — две записи
        /// (<see cref="Touch"/>: метка времени и счётчик), без аллокаций и исключений. Одна сторожевая задача
        /// спит до конца окна; проснувшись, она смотрит, был ли прогресс: не было — дедлайн истёк; был —
        /// досыпает остаток окна от последней отметки. Ожиданий получается «длительность потока / окно»,
        /// а не «по одному на чанк».
        /// </summary>
        private sealed class IdleDeadline : IDisposable
        {
            private readonly ILlmAsyncMarshaler _asyncMarshaler;
            private readonly CancellationTokenSource _target;
            private readonly Action<Exception> _onTimerFault;
            private readonly CancellationTokenSource _stop = new();
            private readonly CancellationToken _stopToken;
            private readonly long _windowTicks;
            private long _lastProgressTimestamp;
            private long _progressCount;
            private int _disposed;
            private int _finishedParties;

            public IdleDeadline(
                ILlmAsyncMarshaler asyncMarshaler,
                CancellationTokenSource target,
                float timeoutSeconds,
                Action<Exception> onTimerFault)
            {
                _asyncMarshaler = asyncMarshaler;
                _target = target;
                _onTimerFault = onTimerFault;
                // ПОЧЕМУ токен снимается заранее: после Dispose обращение к _stop.Token бросает
                // ObjectDisposedException, а сторож может проснуться уже после освобождения.
                _stopToken = _stop.Token;
                double ticks = timeoutSeconds * (double)Stopwatch.Frequency;
                _windowTicks = ticks >= long.MaxValue ? long.MaxValue : Math.Max(1L, (long)ticks);
                _lastProgressTimestamp = Stopwatch.GetTimestamp();
                _ = WatchAsync();
            }

            /// <summary>Отметить прогресс. Без аллокаций и исключений — вызывается на каждый чанк.</summary>
            public void Touch()
            {
                Volatile.Write(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _progressCount);
            }

            private async Task WatchAsync()
            {
                bool elapsed;
                try
                {
                    long remainingTicks = _windowTicks;
                    while (true)
                    {
                        long seen = Volatile.Read(ref _progressCount);
                        await _asyncMarshaler.DelayAsync(ToMilliseconds(remainingTicks), _stopToken)
                            .ConfigureAwait(false);
                        if (_stopToken.IsCancellationRequested)
                        {
                            elapsed = false;
                            break;
                        }

                        if (Volatile.Read(ref _progressCount) == seen)
                        {
                            // Полное окно без единого чанка — это и есть застой.
                            elapsed = true;
                            break;
                        }

                        // Прогресс был: досыпаем остаток окна от последней отметки.
                        long sinceProgress = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastProgressTimestamp);
                        remainingTicks = _windowTicks - sinceProgress;
                        if (remainingTicks <= 0)
                        {
                            elapsed = true;
                            break;
                        }
                    }
                }
                catch (Exception) when (_stopToken.IsCancellationRequested)
                {
                    // Мы сами остановили сторожа: запрос завершился раньше дедлайна. Хост может отдать
                    // остановленную задержку и как отменённую, и как сбойную (мост UniTask→Task), поэтому
                    // фильтр — по факту остановки, а не по типу исключения.
                    elapsed = false;
                }
                catch (Exception ex)
                {
                    // ПОЧЕМУ: сбой таймера — не истечение таймера. Отменить запрос здесь значило бы
                    // сообщить о таймауте, которого не было. Дедлайн остаётся неподкреплённым, а сбой
                    // отдаётся наружу.
                    elapsed = false;
                    try
                    {
                        _onTimerFault?.Invoke(ex);
                    }
                    catch (Exception)
                    {
                        // Обработчик сбоя не должен уронить необслуживаемую задачу сторожа.
                    }
                }
                finally
                {
                    ReleaseStopSource();
                }

                if (!elapsed)
                {
                    return;
                }

                try
                {
                    _target.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (AggregateException)
                {
                }
            }

            private static int ToMilliseconds(long ticks)
            {
                double milliseconds = ticks * 1000d / Stopwatch.Frequency;
                return milliseconds >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(milliseconds));
            }

            /// <summary>
            /// Источник остановки освобождает тот, кто закончил вторым — сторож или владелец, — чтобы ни
            /// один из них не тронул уже освобождённый объект.
            /// </summary>
            private void ReleaseStopSource()
            {
                if (Interlocked.Increment(ref _finishedParties) == 2)
                {
                    _stop.Dispose();
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }

                _stop.Cancel();
                ReleaseStopSource();
            }
        }

        /// <param name="inner">Клиент, вызовы которого ограничиваются по времени.</param>
        /// <param name="timeoutSecondsProvider">
        /// Возвращает таймаут запроса в секундах, читается заново на каждый вызов. Значение &lt;= 0
        /// отключает таймаут (вызов проходит напрямую).
        /// </param>
        /// <param name="asyncMarshaler">Планировщик задержек хоста; Unity подставляет реализацию на PlayerLoop.</param>
        /// <param name="onDeadlineTimerFault">
        /// Вызывается, когда задержка хоста завершилась сбоем (не истечением и не нашей остановкой).
        /// Запрос при этом НЕ отменяется. По умолчанию сбой никуда не сообщается — хост, которому важно
        /// знать о неработающем таймере, передаёт сюда свой лог.
        /// </param>
        public TimeoutLlmClientDecorator(
            ILlmClient inner,
            Func<float> timeoutSecondsProvider,
            ILlmAsyncMarshaler asyncMarshaler = null,
            Action<Exception> onDeadlineTimerFault = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _timeoutSecondsProvider =
                timeoutSecondsProvider ?? throw new ArgumentNullException(nameof(timeoutSecondsProvider));
            _asyncMarshaler = asyncMarshaler ?? PassThroughLlmAsyncMarshaler.Instance;
            _onDeadlineTimerFault = onDeadlineTimerFault;
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCalling => _inner.SupportsNativeToolCalling;

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return _inner.SupportsNativeToolCallingForRole(agentRoleId);
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId, string routingProfileId)
        {
            return _inner.SupportsNativeToolCallingForRole(agentRoleId, routingProfileId);
        }

        /// <inheritdoc />
        public int? ResolveContextWindowTokensForRole(string agentRoleId, string routingProfileId)
        {
            return _inner.ResolveContextWindowTokensForRole(agentRoleId, routingProfileId);
        }

        /// <inheritdoc />
        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _inner.SetTools(tools);
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            float timeoutSeconds = _timeoutSecondsProvider();
            if (timeoutSeconds <= 0f)
            {
                return await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            }

            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using CancellationSignal signal = new(timeoutCts.Token);
            using IdleDeadline deadline = new(_asyncMarshaler, timeoutCts, timeoutSeconds, _onDeadlineTimerFault);

            Task<LlmCompletionResult> operation = null;
            try
            {
                operation = _inner.CompleteAsync(request, timeoutCts.Token);
                LlmCompletionResult result =
                    await AwaitOperationAsync(operation, signal, timeoutCts.Token).ConfigureAwait(false);
                // ПОЧЕМУ: часть внутренних клиентов переводит отменённый связанный токен в результат
                // Cancelled. Этот декоратор — САМЫЙ ВНЕШНИЙ слой (retry/fallback внутри уже видели результат
                // Cancelled, и повторять на сработавшем токене всё равно бесполезно); переписывается только
                // видимая вызывающему типизация, чтобы таймаут библиотеки не выглядел как отмена пользователя.
                if (result != null && !result.Ok && result.ErrorCode == LlmErrorCode.Cancelled &&
                    timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    result.ErrorCode = LlmErrorCode.Timeout;
                }

                return result;
            }
            // Настоящая остановка вызывающим: пропускается без изменений, чтобы обработка отмены отработала.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Связанный таймер сработал при живом токене вызывающего: это таймаут библиотеки.
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new LlmOperationTimeoutException();
            }
            finally
            {
                if (operation != null)
                {
                    _ = ObserveOperationAsync(operation);
                }
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            float timeoutSeconds = _timeoutSecondsProvider();
            if (timeoutSeconds <= 0f)
            {
                await foreach (LlmStreamChunk chunk in _inner
                                   .CompleteStreamingAsync(request, cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    yield return chunk;
                }

                yield break;
            }

            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using CancellationSignal signal = new(timeoutCts.Token);
            using IdleDeadline deadline = new(_asyncMarshaler, timeoutCts, timeoutSeconds, _onDeadlineTimerFault);

            IAsyncEnumerator<LlmStreamChunk> enumerator =
                _inner.CompleteStreamingAsync(request, timeoutCts.Token).GetAsyncEnumerator(timeoutCts.Token);
            Task<bool> pendingMove = null;
            try
            {
                while (true)
                {
                    LlmStreamChunk current = null;
                    bool hasNext = false;
                    bool timedOut = false;

                    try
                    {
                        pendingMove = enumerator.MoveNextAsync().AsTask();
                        hasNext = await AwaitOperationAsync(pendingMove, signal, timeoutCts.Token).ConfigureAwait(false);
                        current = hasNext ? enumerator.Current : null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    {
                        timedOut = true;
                    }

                    if (timedOut)
                    {
                        yield return new LlmStreamChunk
                        {
                            IsDone = true,
                            Error = "LLM request timed out.",
                            ErrorCode = LlmErrorCode.Timeout
                        };
                        yield break;
                    }

                    if (!hasNext)
                    {
                        yield break;
                    }

                    // Бюджет простоя, а не всего хода: каждый чанк — прогресс. Отметка без аллокаций.
                    deadline.Touch();

                    // ПОЧЕМУ: терминальный чанк Cancelled может быть переводом внутренним клиентом таймаута
                    // связанного токена этого декоратора — чанк сохраняется, исправляется только код.
                    // ПОЧЕМУ copy-on-write: экземпляр чанка может кэшироваться внутренним клиентом, поэтому
                    // исправление собирает новый чанк, а не правит полученный.
                    if (current != null && current.IsDone && current.ErrorCode == LlmErrorCode.Cancelled &&
                        timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        current = new LlmStreamChunk
                        {
                            Text = current.Text,
                            ReasoningText = current.ReasoningText,
                            IsDone = current.IsDone,
                            Error = "LLM request timed out.",
                            ErrorCode = LlmErrorCode.Timeout,
                            HttpStatus = current.HttpStatus,
                            RetryAfterSeconds = current.RetryAfterSeconds,
                            Model = current.Model,
                            PromptTokens = current.PromptTokens,
                            LastRoundtripPromptTokens = current.LastRoundtripPromptTokens,
                            CompletionTokens = current.CompletionTokens,
                            TotalTokens = current.TotalTokens,
                            CacheReadTokens = current.CacheReadTokens,
                            CacheWriteTokens = current.CacheWriteTokens,
                            ExecutedToolCalls = current.ExecutedToolCalls,
                            StartsNewMessage = current.StartsNewMessage,
                            BufferedStreamingUseToolProgressHint = current.BufferedStreamingUseToolProgressHint,
                            BufferedStreamingNoToolBinding = current.BufferedStreamingNoToolBinding
                        };
                    }

                    yield return current;
                }
            }
            finally
            {
                if (timeoutCts.IsCancellationRequested || (pendingMove != null && !pendingMove.IsCompleted))
                {
                    _ = DisposeAfterOperationAsync(pendingMove ?? Task.CompletedTask, enumerator);
                }
                else
                {
                    Task disposal = enumerator.DisposeAsync().AsTask();
                    if (!disposal.IsCompleted)
                    {
                        await Task.WhenAny(disposal, signal.Task).ConfigureAwait(false);
                    }
                    if (disposal.IsCompleted)
                    {
                        await disposal.ConfigureAwait(false);
                    }
                    else
                    {
                        _ = ObserveOperationAsync(disposal);
                        cancellationToken.ThrowIfCancellationRequested();
                        // A timeout chunk has already been delivered when cancellation won MoveNext;
                        // its cleanup must not replace that outcome with another error.
                    }
                }
            }
        }
    }
}
