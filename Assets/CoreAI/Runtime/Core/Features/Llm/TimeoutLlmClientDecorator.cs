#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Portable-core request timeout for an <see cref="ILlmClient"/>. Bounds the total wall-clock of a
    /// single logical request on BOTH <see cref="CompleteAsync"/> and <see cref="CompleteStreamingAsync"/>
    /// by cancelling a linked token after <c>timeoutSeconds</c> (read live so a settings hot-swap takes
    /// effect on the next call). A library timeout — where the injected timer fired but the caller's own
    /// token was NOT cancelled — is surfaced as <see cref="LlmOperationTimeoutException"/> on the
    /// non-streaming path and as a terminal <see cref="LlmErrorCode.Timeout"/> chunk on the streaming path;
    /// a genuine caller cancellation always propagates unchanged.
    /// <para>
    /// This lives in <c>CoreAI.Core</c> so headless hosts, tests and non-Unity consumers get a request
    /// timeout too — previously it was only enforced by the Unity <c>CoreAiChatService</c>. On WebGL the
    /// single-threaded player still relies on the Unity PlayerLoop-based <c>CancelAfterSlim</c> timer in
    /// <c>CoreAiChatService</c> (a managed <see cref="CancellationTokenSource"/> timer is unreliable
    /// there); the two target the same <see cref="ICoreAISettings.LlmRequestTimeoutSeconds"/> and are
    /// additive — whichever fires first cancels the shared linked token.
    /// </para>
    /// </summary>
    public sealed class TimeoutLlmClientDecorator : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly Func<float> _timeoutSecondsProvider;

        /// <param name="inner">The client whose calls are time-bounded.</param>
        /// <param name="timeoutSecondsProvider">
        /// Returns the request timeout in seconds, read fresh per call. A value &lt;= 0 disables the
        /// timeout (the call delegates straight through).
        /// </param>
        public TimeoutLlmClientDecorator(ILlmClient inner, Func<float> timeoutSecondsProvider)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _timeoutSecondsProvider =
                timeoutSecondsProvider ?? throw new ArgumentNullException(nameof(timeoutSecondsProvider));
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCalling => _inner.SupportsNativeToolCalling;

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return _inner.SupportsNativeToolCallingForRole(agentRoleId);
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
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                LlmCompletionResult result =
                    await _inner.CompleteAsync(request, timeoutCts.Token).ConfigureAwait(false);
                // WHY: Some inner clients translate their cancelled linked token into a Cancelled result,
                // so the decorator must restore timeout ownership before retry and fallback policies see it.
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
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

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

                    // WHY: A terminal Cancelled chunk may be the inner client's translation of this
                    // decorator's linked-token timeout, so preserve the chunk and correct only its code.
                    if (current != null && current.IsDone && current.ErrorCode == LlmErrorCode.Cancelled &&
                        timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        current.ErrorCode = LlmErrorCode.Timeout;
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
