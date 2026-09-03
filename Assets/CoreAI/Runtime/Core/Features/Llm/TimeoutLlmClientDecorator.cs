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
    /// Portable-core request timeout for an <see cref="ILlmClient"/>. Cancels a linked token after
    /// <c>timeoutSeconds</c> (read live so a settings hot-swap takes effect on the next call).
    /// <para>
    /// <see cref="CompleteAsync"/> bounds the total wall-clock of one non-streaming call.
    /// <see cref="CompleteStreamingAsync"/> is an IDLE / no-progress budget: because a streamed call is the
    /// outermost wrapper around the whole tool-calling turn (model → tool calls → model → …), a fixed total
    /// budget would truncate a healthy long turn once its step durations summed past the timeout. Instead
    /// each yielded chunk re-arms the deadline, so only a genuine stall (no chunk for the full window) times
    /// out — matching the idle deadline the Unity <c>CoreAiChatService</c> applies one layer out.
    /// </para>
    /// A library timeout — where the injected timer fired but the caller's own
    /// token was NOT cancelled — is surfaced as <see cref="LlmOperationTimeoutException"/> on the
    /// non-streaming path and as a terminal <see cref="LlmErrorCode.Timeout"/> chunk on the streaming path;
    /// a genuine caller cancellation always propagates unchanged.
    /// <para>
    /// This lives in <c>CoreAI.Core</c> so headless hosts, tests and non-Unity consumers get a request
    /// timeout too. Deadline scheduling is delegated to <see cref="ILlmAsyncMarshaler.DelayAsync"/>;
    /// Unity supplies a PlayerLoop-driven delay so this decorator itself remains effective in WebGL,
    /// while portable hosts retain the default managed task delay.
    /// </para>
    /// </summary>
    public sealed class TimeoutLlmClientDecorator : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly Func<float> _timeoutSecondsProvider;
        private readonly ILlmAsyncMarshaler _asyncMarshaler;

        private sealed class HostScheduledCancellationDeadline : IDisposable
        {
            private readonly object _gate = new();
            private readonly ILlmAsyncMarshaler _asyncMarshaler;
            private readonly CancellationTokenSource _target;
            private CancellationTokenSource _delayCts;
            private long _generation;
            private bool _disposed;

            public HostScheduledCancellationDeadline(
                ILlmAsyncMarshaler asyncMarshaler,
                CancellationTokenSource target)
            {
                _asyncMarshaler = asyncMarshaler;
                _target = target;
            }

            public void Reset(float timeoutSeconds)
            {
                double totalMilliseconds = timeoutSeconds * 1000d;
                int milliseconds = totalMilliseconds >= int.MaxValue
                    ? int.MaxValue
                    : Math.Max(1, (int)Math.Ceiling(totalMilliseconds));
                CancellationTokenSource next = new();
                CancellationTokenSource previous;
                long generation;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        next.Dispose();
                        return;
                    }

                    previous = _delayCts;
                    _delayCts = next;
                    generation = ++_generation;
                }

                previous?.Cancel();
                previous?.Dispose();
                _ = CancelWhenElapsedAsync(milliseconds, generation, next.Token);
            }

            private async Task CancelWhenElapsedAsync(
                int milliseconds,
                long generation,
                CancellationToken cancellationToken)
            {
                try
                {
                    await _asyncMarshaler.DelayAsync(milliseconds, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                }

                lock (_gate)
                {
                    if (_disposed || generation != _generation)
                    {
                        return;
                    }
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

            public void Dispose()
            {
                CancellationTokenSource delayCts;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    _generation++;
                    delayCts = _delayCts;
                    _delayCts = null;
                }

                delayCts?.Cancel();
                delayCts?.Dispose();
            }
        }

        /// <param name="inner">The client whose calls are time-bounded.</param>
        /// <param name="timeoutSecondsProvider">
        /// Returns the request timeout in seconds, read fresh per call. A value &lt;= 0 disables the
        /// timeout (the call delegates straight through).
        /// </param>
        /// <param name="asyncMarshaler">Host delay scheduler; Unity supplies its PlayerLoop implementation.</param>
        public TimeoutLlmClientDecorator(
            ILlmClient inner,
            Func<float> timeoutSecondsProvider,
            ILlmAsyncMarshaler asyncMarshaler = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _timeoutSecondsProvider =
                timeoutSecondsProvider ?? throw new ArgumentNullException(nameof(timeoutSecondsProvider));
            _asyncMarshaler = asyncMarshaler ?? PassThroughLlmAsyncMarshaler.Instance;
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
            using HostScheduledCancellationDeadline deadline =
                new(_asyncMarshaler, timeoutCts);
            deadline.Reset(timeoutSeconds);

            try
            {
                LlmCompletionResult result =
                    await _inner.CompleteAsync(request, timeoutCts.Token).ConfigureAwait(false);
                // WHY: Some inner clients translate the cancelled linked token into a Cancelled result.
                // This decorator is the OUTERMOST layer (retry/fallback run inside and have already seen
                // the Cancelled result - retrying on the fired token is futile anyway); the rewrite fixes
                // the CALLER-visible typing so a library timeout is not reported as user cancellation.
                if (result != null && !result.Ok && result.ErrorCode == LlmErrorCode.Cancelled &&
                    timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    result.ErrorCode = LlmErrorCode.Timeout;
                }

                return result;
            }
            // Genuine caller stop: propagate untouched so user-cancellation handling still runs.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // The linked timer fired while the caller's own token stayed live: this is a library timeout.
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new LlmOperationTimeoutException();
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
            using HostScheduledCancellationDeadline deadline =
                new(_asyncMarshaler, timeoutCts);
            deadline.Reset(timeoutSeconds);

            IAsyncEnumerator<LlmStreamChunk> enumerator =
                _inner.CompleteStreamingAsync(request, timeoutCts.Token).GetAsyncEnumerator(timeoutCts.Token);
            try
            {
                while (true)
                {
                    LlmStreamChunk current = null;
                    bool hasNext = false;
                    bool timedOut = false;

                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
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

                    // WHY: idle budget, not a whole-turn one — a streamed call wraps the entire multi-step
                    // tool-calling turn, so every chunk is progress and re-arms the deadline. Only a real
                    // stall (no chunk for the full window) fires. Mirrors CoreAiChatService's idle timer.
                    deadline.Reset(timeoutSeconds);

                    // WHY: A terminal Cancelled chunk may be the inner client's translation of this
                    // decorator's linked-token timeout, so preserve the chunk and correct only its code.
                    // WHY: Copy-on-write — the chunk instance may be cached or reused by the inner
                    // client, so the correction builds a new chunk instead of mutating the received one.
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
                            BufferedStreamingUseToolProgressHint = current.BufferedStreamingUseToolProgressHint,
                            BufferedStreamingNoToolBinding = current.BufferedStreamingNoToolBinding
                        };
                    }

                    yield return current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
#endif
