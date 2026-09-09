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
            // WHY no RunContinuationsAsynchronously: that flag defers the continuation to the thread
            // pool, which WebGL does not have — the conversion lint refuses it in WebGL-reachable code.
            // So TrySetResult below resumes the waiter INLINE, inside the IdleDeadline timer's call to
            // CancellationTokenSource.Cancel(). That is contained on purpose: the unwind that follows
            // only records an outcome in locals, and every Dispose — including the one for this very
            // token source — happens in RunCompleteWithDeadlineAsync's finally, after the inline
            // resumption has already returned.
            private readonly TaskCompletionSource<bool> _source = new();
            private readonly CancellationTokenRegistration _registration;

            public CancellationSignal(CancellationToken token)
            {
                _registration = token.Register(() => _source.TrySetResult(true));
            }

            public Task Task => _source.Task;
            public void Dispose() => _registration.Dispose();
        }

        // Bounded real-time grace window given to a cooperative operation to finish its own unwind
        // after the deadline fires, before its (possibly partial) result is discarded as a bare
        // timeout. Kept short: it only needs to cover the operation's own cancellation handling, not
        // add a second de-facto deadline.
        private const int GraceWindowMilliseconds = 20;

        private static async Task<T> AwaitOperationAsync<T>(Task<T> operation, CancellationSignal signal,
            CancellationToken token, ILlmAsyncMarshaler asyncMarshaler)
        {
            if (!operation.IsCompleted)
            {
                await Task.WhenAny(operation, signal.Task);
                if (!operation.IsCompleted)
                {
                    // WHY a single bounded wait, not a per-iteration Task.Yield spin loop: a cooperative
                    // inner operation observes the SAME cancellation that just woke `signal` and may
                    // still be mid-unwind. Which of the two token callbacks the runtime invokes first is
                    // an implementation detail (not a documented contract) — .NET commonly invokes them
                    // in reverse registration order, but that is not guaranteed — so a single scheduler
                    // hop is not guaranteed to be enough. A bounded Task.Delay gives it the same genuine
                    // real-time window to finish before its data is discarded, while still giving up
                    // promptly for a backend that never reacts at all — without the cost of a spin loop:
                    // under Unity's PlayerLoop-driven SynchronizationContext, each Task.Yield()
                    // continuation is posted to the NEXT frame, so looping per-check over this window
                    // could cost a whole frame (or more, at low FPS) per iteration.
                    await Task.WhenAny(
                        operation,
                        asyncMarshaler.DelayAsync(GraceWindowMilliseconds, CancellationToken.None));
                }

                if (!operation.IsCompleted)
                {
                    throw new OperationCanceledException(token);
                }
            }

            return await operation;
        }

        private static async Task ObserveOperationAsync(Task operation)
        {
            try
            {
                await operation;
            }
            catch (Exception)
            {
                // The caller has already received the deadline/cancellation outcome. Observing
                // this eventual failure prevents an unobserved background task exception.
            }
        }

        private static async Task DisposeAfterOperationAsync(Task operation, IAsyncEnumerator<LlmStreamChunk> enumerator)
        {
            await ObserveOperationAsync(operation);
            try
            {
                await enumerator.DisposeAsync();
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
            private int _elapsed;

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

            /// <summary>Whether the idle window ran out, as opposed to the request finishing or the
            /// caller cancelling. Asked instead of the token source's own state, which a disposed
            /// source can refuse to answer.</summary>
            public bool Elapsed => Volatile.Read(ref _elapsed) == 1;

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
                        await _asyncMarshaler.DelayAsync(ToMilliseconds(remainingTicks), _stopToken);
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

                // WHY the flag is set BEFORE Cancel(): without RunContinuationsAsynchronously the
                // waiter resumes inline inside that call, and it asks this flag which cancellation
                // it is unwinding for.
                Volatile.Write(ref _elapsed, 1);

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
        // WHY not `async Task<T>` all the way through: the compiler puts an async method's own Task into
        // the Canceled state (not Faulted) when its body throws an OperationCanceledException-derived
        // exception — including LlmOperationTimeoutException. A caller that re-observes an already-completed
        // Canceled task, rather than awaiting it live (e.g. after racing it via Task.WhenAny, as
        // Assert.ThrowsAsync does following an earlier await), is not guaranteed the exact exception
        // instance/type back on every runtime — the deadline can surface as a bare TaskCanceledException,
        // losing the fact that it was specifically a TIMEOUT. TaskCompletionSource.TrySetException has no
        // such special case: it always produces a Faulted task that reliably replays the exact exception.
        public Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            float timeoutSeconds = _timeoutSecondsProvider();
            if (timeoutSeconds <= 0f)
            {
                return _inner.CompleteAsync(request, cancellationToken);
            }

            // WHY the outcome is held in locals and published AFTER the finally instead of using a
            // TaskCompletionSource created with RunContinuationsAsynchronously: that flag needs a thread
            // pool, which WebGL does not have (the conversion lint refuses it in WebGL-reachable code).
            // Publishing last gives the same guarantee for free — the caller's continuation, inline or
            // not, cannot start before deadline/signal/timeoutCts are disposed, so a next request can
            // never overlap the previous request's live IdleDeadline timer.
            TaskCompletionSource<LlmCompletionResult> tcs = new();
            _ = RunCompleteWithDeadlineAsync(request, timeoutSeconds, cancellationToken, tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Whether OUR linked source is the one that fired. Asked in the catch body, never in an
        /// exception filter: on a disposed source this property throws on some runtimes, and a filter
        /// that throws is silently treated as "does not match" — the library timeout then fell
        /// through to the generic cancel branch and the caller got a bare TaskCanceledException.
        /// </summary>
        private static bool WasCancelled(CancellationTokenSource source)
        {
            try
            {
                return source is { IsCancellationRequested: true };
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private async Task RunCompleteWithDeadlineAsync(
            LlmCompletionRequest request,
            float timeoutSeconds,
            CancellationToken cancellationToken,
            TaskCompletionSource<LlmCompletionResult> tcs)
        {
            // WHY resource creation is INSIDE the try, not in `using` declarations above it: this method
            // runs detached (`_ = RunCompleteWithDeadlineAsync(...)`), so any exception that escapes it
            // becomes an unobserved task exception and `tcs` is never completed — the caller awaiting
            // `tcs.Task` hangs forever. Every path out of this method, including a throw from
            // CreateLinkedTokenSource/CancellationSignal/IdleDeadline construction, or from disposing
            // them afterward in `finally` below, must complete `tcs`.
            CancellationTokenSource timeoutCts = null;
            CancellationSignal signal = null;
            IdleDeadline deadline = null;
            Task<LlmCompletionResult> operation = null;
            LlmCompletionResult completed = null;
            Exception failure = null;
            bool cancelled = false;
            CancellationToken cancelledWith = default;
            try
            {
                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                signal = new CancellationSignal(timeoutCts.Token);
                deadline = new IdleDeadline(_asyncMarshaler, timeoutCts, timeoutSeconds, _onDeadlineTimerFault);

                operation = _inner.CompleteAsync(request, timeoutCts.Token);
                LlmCompletionResult result =
                    await AwaitOperationAsync(operation, signal, timeoutCts.Token, _asyncMarshaler);
                // ПОЧЕМУ: часть внутренних клиентов переводит отменённый связанный токен в результат
                // Cancelled. Этот декоратор — САМЫЙ ВНЕШНИЙ слой (retry/fallback внутри уже видели результат
                // Cancelled, и повторять на сработавшем токене всё равно бесполезно); переписывается только
                // видимая вызывающему типизация, чтобы таймаут библиотеки не выглядел как отмена пользователя.
                if (result != null && !result.Ok && result.ErrorCode == LlmErrorCode.Cancelled &&
                    deadline.Elapsed && !cancellationToken.IsCancellationRequested)
                {
                    result.ErrorCode = LlmErrorCode.Timeout;
                }

                completed = result;
            }
            // WHY one catch that classifies in its body instead of three `when` filters on token state:
            // an exception filter that touches timeoutCts throws ObjectDisposedException on runtimes
            // where a disposed CancellationTokenSource refuses IsCancellationRequested, and a filter that
            // throws is silently treated as "does not match" — the library timeout then fell through to
            // the generic cancel branch and the caller saw a bare TaskCanceledException instead of
            // LlmOperationTimeoutException. The deadline's own flag answers the same question without
            // touching a token at all.
            catch (OperationCanceledException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // Настоящая остановка вызывающим: статус остаётся Canceled, как и раньше.
                    cancelled = true;
                    cancelledWith = cancellationToken;
                }
                else if (deadline is { Elapsed: true } || WasCancelled(timeoutCts))
                {
                    // Таймер сработал при живом токене вызывающего: это таймаут библиотеки — Faulted,
                    // чтобы тип исключения переживал повторное наблюдение независимо от рантайма.
                    failure = new LlmOperationTimeoutException();
                }
                else if (exception is LlmOperationTimeoutException)
                {
                    // WHY this branch exists: these decorators nest, and LlmOperationTimeoutException
                    // IS an OperationCanceledException. An inner decorator with a shorter deadline
                    // surfaces exactly that exception here, through _inner.CompleteAsync, while THIS
                    // decorator's own deadline has not elapsed and the caller's token has not fired
                    // either — the two checks above both say no. Without this branch that exception
                    // falls into the generic "inner cancelled for its own reasons" case below and
                    // TrySetCanceled discards it, so the caller sees a bare cancellation instead of
                    // the timeout this file exists to preserve. Faulted, like the branch above, so the
                    // exact type survives re-observation.
                    failure = exception;
                }
                else
                {
                    // Neither the caller's token nor ours fired: the inner client cancelled for its own
                    // reasons. Still a cancellation, not a failure.
                    cancelled = true;
                    cancelledWith = exception.CancellationToken;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (operation != null)
                {
                    _ = ObserveOperationAsync(operation);
                }

                // WHY each dispose is wrapped in its own try/catch instead of left to throw through:
                // this method runs detached (`_ = RunCompleteWithDeadlineAsync(...)`), so an exception
                // escaping `finally` would never reach the completion calls below — it becomes an
                // unobserved task exception and `tcs` is never completed, hanging the caller forever.
                // (A live host-delay callback throwing out of `IdleDeadline.Dispose()`'s `_stop.Cancel()`
                // is exactly this shape.) Each dispose is attempted independently so one throwing does
                // not skip the other two; the first exception seen wins, matching how this file already
                // swallows secondary failures elsewhere (e.g. inside IdleDeadline itself).
                try
                {
                    deadline?.Dispose();
                }
                catch (Exception disposalException)
                {
                    failure ??= disposalException;
                }

                try
                {
                    signal?.Dispose();
                }
                catch (Exception disposalException)
                {
                    failure ??= disposalException;
                }

                try
                {
                    timeoutCts?.Dispose();
                }
                catch (Exception disposalException)
                {
                    failure ??= disposalException;
                }
            }

            // WHY completion happens here, after the try/finally above, instead of as soon as an outcome
            // is known: the caller's continuation on `tcs.Task` — inline or not — must not be able to run
            // before deadline/signal/timeoutCts are disposed, or a next request could overlap this
            // request's still-live IdleDeadline timer. That ordering holds on every path above, including
            // the ones where disposal itself throws, because the throwing dispose is caught above (not
            // left to skip straight past the other two disposals and this completion) before any of the
            // three TrySet* calls below run.
            if (failure != null)
            {
                tcs.TrySetException(failure);
            }
            else if (cancelled)
            {
                tcs.TrySetCanceled(cancelledWith);
            }
            else
            {
                tcs.TrySetResult(completed);
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
                                   .WithCancellation(cancellationToken))
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
                        hasNext = await AwaitOperationAsync(
                            pendingMove, signal, timeoutCts.Token, _asyncMarshaler);
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
                        await Task.WhenAny(disposal, signal.Task);
                    }
                    if (disposal.IsCompleted)
                    {
                        await disposal;
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
