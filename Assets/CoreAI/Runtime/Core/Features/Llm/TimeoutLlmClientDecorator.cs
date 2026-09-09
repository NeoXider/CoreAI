using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
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
            // WHY no RunContinuationsAsynchronously: this source is awaited through Task.WhenAny, and
            // WhenAny's own internal continuation does NOT capture a synchronization context. With the
            // flag, completing this source hands that continuation to the thread pool - which a WebGL
            // player does not have, so the wait never ends. Without the flag WhenAny completes inline on
            // the cancelling stack and our own continuation (we await WITHOUT ConfigureAwait(false))
            // posts to the host loop. Inline completion is safe here: we only post, and no
            // CancellationTokenSource lock is held across it.
            private readonly TaskCompletionSource<bool> _source = new();
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
                await Task.WhenAny(operation, signal.Task);
                if (!operation.IsCompleted)
                {
                    // WHY one yield before giving up: the signal and the inner client watch the SAME
                    // token, and a cooperative inner client answers a stop with a terminal Cancelled
                    // chunk rather than an exception. Winning that race by nanoseconds would throw away
                    // an answer that already exists and turn a clean stop into a raw exception for the
                    // caller. The previous code got this bias for free from a thread-pool hop
                    // (RunContinuationsAsynchronously on the signal), which is exactly the trick that is
                    // dead in a WebGL player - so the bias is now explicit and host-scheduled: a yield
                    // posts to the host loop where a pool queue would never run.
                    await Task.Yield();
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
        /// Races one pending <c>MoveNextAsync</c> against the request's cancellation signal without
        /// allocating anything per chunk.
        /// <para>
        /// WHY it exists: the previous per-chunk wait was <c>move.AsTask()</c> + an <c>async</c> helper +
        /// <c>Task.WhenAny(operation, signal)</c>. Per chunk that arrives asynchronously (the normal case on a
        /// real network stream) that is a wrapper <see cref="Task"/>, a state-machine box with its own
        /// <see cref="Task"/>, the <c>params</c> array of <c>WhenAny</c>, the <c>WhenAny</c> promise and a
        /// posted continuation - five or six heap objects for every token a learner reads. This type is one
        /// object per request: it is a reusable <see cref="IValueTaskSource{TResult}"/> that the loop awaits
        /// directly, its move-completion callback is one cached delegate, and the cancellation registration
        /// is taken once. Only the timeout path allocates (the exception it reports).
        /// </para>
        /// <para>
        /// The guarantee is unchanged: the wait ends when the signal fires even if the inner client never
        /// observes its token. A move that completes after the signal won is still consumed exactly once,
        /// on the "late" side: its result is kept for <see cref="TryClaimLateResult"/> (the one-hop bias
        /// that prefers an answer which already exists over reporting a timeout) and
        /// <see cref="AbandonedMoveCompletion"/> lets the enumerator be disposed only after that move has
        /// finished, never overlapping an active <c>MoveNext</c>.
        /// </para>
        /// </summary>
        private sealed class MoveNextRace : IValueTaskSource<bool>, IDisposable
        {
            private const int Idle = 0;
            private const int Racing = 1;
            private const int MoveWon = 2;
            private const int SignalWon = 3;

            private readonly CancellationToken _token;
            private readonly Action _onMoveCompleted;
            private readonly CancellationTokenRegistration _registration;
            private readonly object _lateGate = new();

            // WHY RunContinuationsAsynchronously stays false: the loop awaits this source WITHOUT
            // ConfigureAwait(false), so a host SynchronizationContext is captured at registration and the
            // continuation is posted to it on completion regardless of the flag. Where there is no context
            // the continuation runs inline on the completing stack - exactly what the former inline
            // TaskCompletionSource + WhenAny chain did - instead of being queued to a thread pool the WebGL
            // player does not have.
            private ManualResetValueTaskSourceCore<bool> _core;
            private ValueTaskAwaiter<bool> _pending;
            private int _state;
            private volatile bool _signaled;

            private bool _lateAvailable;
            private bool _lateHasNext;
            private Exception _lateError;
            private TaskCompletionSource<bool> _abandoned;

            public MoveNextRace(CancellationToken token)
            {
                _token = token;
                _onMoveCompleted = OnMoveCompleted;
                _registration = token.Register(OnSignal);
            }

            /// <summary>
            /// Whether a move was left running after the signal won and nobody claimed its outcome yet.
            /// Only <see cref="TryClaimLateResult"/> leaves that state.
            /// </summary>
            public bool HasAbandonedMove => Volatile.Read(ref _state) == SignalWon;

            /// <summary>
            /// Completes once an abandoned move has finished (its outcome is observed here, never thrown).
            /// Already completed when there is no abandoned move.
            /// </summary>
            public Task AbandonedMoveCompletion
            {
                get
                {
                    lock (_lateGate)
                    {
                        if (Volatile.Read(ref _state) != SignalWon || _lateAvailable)
                        {
                            return Task.CompletedTask;
                        }

                        _abandoned ??= new TaskCompletionSource<bool>();
                        return _abandoned.Task;
                    }
                }
            }

            /// <summary>Waits for an incomplete move or the signal, whichever comes first.</summary>
            public ValueTask<bool> WaitAsync(ValueTask<bool> move)
            {
                if (Volatile.Read(ref _state) != Idle)
                {
                    throw new InvalidOperationException("A MoveNext race is already in flight.");
                }

                // WHY this order: the core is reset and the awaiter stored BEFORE the round opens, so a
                // signal that lands right after the state flip completes a fresh core (a reset after
                // SetException would discard the completion and the wait would never end), and the
                // move callback can read the awaiter the moment it is registered.
                _core.Reset();
                // WHY the callback keeps the scheduling context (no ConfigureAwait(false)): a Task-backed
                // move that completes on a host thread with a derived SynchronizationContext (the Unity
                // main thread) is not inlined by .NET but queued to the thread pool when its awaiter asked
                // for no context - and a WebGL player has no thread pool, so the callback would never run
                // and the stream would hang. With the context the inner posts the callback to the host
                // loop: one small post per chunk, and still no Task, WhenAny or state machine.
                _pending = move.GetAwaiter();
                Volatile.Write(ref _state, Racing);
                _pending.UnsafeOnCompleted(_onMoveCompleted);
                if (_signaled)
                {
                    TrySignal();
                }

                return new ValueTask<bool>(this, _core.Version);
            }

            /// <summary>
            /// Hands over the outcome of a move that finished after the signal won. True at most once per
            /// abandoned move; after a successful claim the race can be used for the next chunk.
            /// </summary>
            public bool TryClaimLateResult(out bool hasNext, out Exception error)
            {
                lock (_lateGate)
                {
                    if (Volatile.Read(ref _state) != SignalWon || !_lateAvailable)
                    {
                        hasNext = false;
                        error = null;
                        return false;
                    }

                    hasNext = _lateHasNext;
                    error = _lateError;
                    _lateAvailable = false;
                    _lateHasNext = false;
                    _lateError = null;
                    Volatile.Write(ref _state, Idle);
                    return true;
                }
            }

            public void Dispose()
            {
                _registration.Dispose();
            }

            bool IValueTaskSource<bool>.GetResult(short token)
            {
                try
                {
                    return _core.GetResult(token);
                }
                finally
                {
                    // The signal's round stays SignalWon so the late side can still be claimed or awaited.
                    Interlocked.CompareExchange(ref _state, Idle, MoveWon);
                }
            }

            ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);

            void IValueTaskSource<bool>.OnCompleted(
                Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                _core.OnCompleted(continuation, state, token, flags);
            }

            private void OnSignal()
            {
                _signaled = true;
                TrySignal();
            }

            private void TrySignal()
            {
                if (Interlocked.CompareExchange(ref _state, SignalWon, Racing) == Racing)
                {
                    _core.SetException(new OperationCanceledException(_token));
                }
            }

            private void OnMoveCompleted()
            {
                if (Interlocked.CompareExchange(ref _state, MoveWon, Racing) == Racing)
                {
                    try
                    {
                        _core.SetResult(_pending.GetResult());
                    }
                    catch (Exception ex)
                    {
                        _core.SetException(ex);
                    }

                    return;
                }

                // The signal won this round: consume the move here so the inner source is released, and
                // keep the outcome for the late side.
                bool hasNext = false;
                Exception error = null;
                try
                {
                    hasNext = _pending.GetResult();
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                TaskCompletionSource<bool> abandoned;
                lock (_lateGate)
                {
                    _lateHasNext = hasNext;
                    _lateError = error;
                    _lateAvailable = true;
                    abandoned = _abandoned;
                }

                abandoned?.TrySetResult(true);
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
                return await _inner.CompleteAsync(request, cancellationToken);
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
                    await AwaitOperationAsync(operation, signal, timeoutCts.Token);
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
            using MoveNextRace race = new(timeoutCts.Token);

            IAsyncEnumerator<LlmStreamChunk> enumerator =
                _inner.CompleteStreamingAsync(request, timeoutCts.Token).GetAsyncEnumerator(timeoutCts.Token);
            try
            {
                while (true)
                {
                    LlmStreamChunk current = null;
                    bool hasNext = false;
                    bool timedOut = false;
                    OperationCanceledException lost = null;

                    try
                    {
                        // WHY the fast path: with a buffering transport the next chunk is usually already
                        // in the buffer and MoveNextAsync completes synchronously; awaiting a completed
                        // ValueTask allocates nothing. An incomplete move is raced against the signal by
                        // MoveNextRace, which is one object per request - the former AsTask + async helper
                        // + Task.WhenAny cost five or six heap objects per asynchronously delivered chunk.
                        ValueTask<bool> move = enumerator.MoveNextAsync();
                        hasNext = move.IsCompleted ? await move : await race.WaitAsync(move);
                        current = hasNext ? enumerator.Current : null;
                    }
                    catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
                    {
                        lost = ex;
                    }

                    if (lost != null)
                    {
                        // WHY one yield before giving up: the signal and the inner client watch the SAME
                        // token, and a cooperative inner client answers a stop with a terminal Cancelled
                        // chunk rather than an exception. Winning that race by nanoseconds would throw away
                        // an answer that already exists and turn a clean stop into a raw exception for the
                        // caller. The hop is host-scheduled (a yield posts to the host loop) because the
                        // thread-pool hop that used to provide this bias for free is dead in a WebGL player.
                        bool recovered = false;
                        if (race.HasAbandonedMove)
                        {
                            await Task.Yield();
                            if (race.TryClaimLateResult(out bool lateHasNext, out Exception lateError))
                            {
                                if (lateError == null)
                                {
                                    recovered = true;
                                    hasNext = lateHasNext;
                                    current = hasNext ? enumerator.Current : null;
                                }
                                else if (lateError is OperationCanceledException lateCancel &&
                                         timeoutCts.IsCancellationRequested)
                                {
                                    lost = lateCancel;
                                }
                                else
                                {
                                    ExceptionDispatchInfo.Capture(lateError).Throw();
                                }
                            }
                        }

                        if (!recovered)
                        {
                            // A genuine stop by the caller propagates unchanged; a deadline at a live
                            // caller token is the library timeout.
                            if (cancellationToken.IsCancellationRequested)
                            {
                                ExceptionDispatchInfo.Capture(lost).Throw();
                            }

                            timedOut = true;
                        }
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
                if (timeoutCts.IsCancellationRequested || race.HasAbandonedMove)
                {
                    // WHY: a move the signal beat may still be running inside the inner client; disposing
                    // the enumerator now would overlap its active MoveNext. The disposal waits for that move
                    // (whose outcome the race has already observed) without holding the caller.
                    _ = DisposeAfterOperationAsync(race.AbandonedMoveCompletion, enumerator);
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
