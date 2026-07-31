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
    /// Adds retry to the STREAMING path of an <see cref="ILlmClient"/>, which the non-streaming
    /// <see cref="LoggingLlmClientDecorator"/> HTTP-retry loop never covered (streaming was single-shot).
    /// A stream is retried only <b>before it commits</b> — i.e. before any chunk with visible
    /// <see cref="LlmStreamChunk.Text"/> or an <see cref="LlmStreamChunk.ExecutedToolCalls"/> entry has been
    /// yielded. Once real content is out the door, re-running would duplicate output or re-fire tool side
    /// effects, so post-commit failures propagate unchanged (identical safety model to
    /// <c>FallbackLlmClientDecorator</c>, but retrying the SAME inner client instead of switching backends).
    /// <para>
    /// Retried triggers (pre-commit only): a thrown exception, a terminal error chunk whose
    /// <see cref="LlmStreamChunk.ErrorCode"/> is transient, or a stream that ends without ever committing.
    /// Caller cancellation is never retried. <see cref="CompleteAsync"/> just delegates — its retry already
    /// lives in the outer logging decorator.
    /// </para>
    /// </summary>
    public sealed class RetryingStreamingLlmClientDecorator : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly int _maxRetryAttempts;
        private readonly Func<int, TimeSpan> _retryDelay;
        private readonly Action<string> _log;

        /// <param name="inner">The streaming client to protect.</param>
        /// <param name="maxRetryAttempts">
        /// Extra attempts after the first (min 0). Total stream opens = <c>maxRetryAttempts + 1</c>.
        /// </param>
        /// <param name="retryDelay">
        /// Maps a zero-based retry index to a wait before the next attempt. <c>null</c> = no delay
        /// (used by deterministic tests). Production passes an exponential/jittered backoff.
        /// </param>
        /// <param name="log">Optional one-line diagnostics sink.</param>
        public RetryingStreamingLlmClientDecorator(
            ILlmClient inner,
            int maxRetryAttempts,
            Func<int, TimeSpan> retryDelay = null,
            Action<string> log = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _maxRetryAttempts = maxRetryAttempts < 0 ? 0 : maxRetryAttempts;
            _retryDelay = retryDelay;
            _log = log;
        }

        /// <summary>Total number of stream retries performed (diagnostics / tests).</summary>
        public int RetryCount { get; private set; }

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
        public Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _inner.CompleteAsync(request, cancellationToken);
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            for (int attempt = 0;; attempt++)
            {
                bool committed = false;
                bool retryablePreCommitFailure = false;
                LlmStreamChunk terminalErrorChunk = null;

                IAsyncEnumerator<LlmStreamChunk> enumerator =
                    _inner.CompleteStreamingAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (!committed)
                    {
                        LlmStreamChunk current = null;
                        bool hasNext = false;
                        LlmStreamChunk transportFailure = null;

                        try
                        {
                            hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                            current = hasNext ? enumerator.Current : null;
                        }
                        catch (OperationCanceledException)
                        {
                            // WHY: The OCE may carry a token other than the caller's (timeout decorator's
                            // linked CTS, per-read idle timer). Retrying it would re-open the stream against
                            // a backend that just timed out, so all cancellation propagates unchanged.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            transportFailure = DescribeTransportFailure(ex);
                        }

                        if (transportFailure != null)
                        {
                            // WHY: A permanent refusal (payment required, expired auth, malformed request)
                            // answers every replay identically. Retrying it only multiplies the user's wait
                            // by the retry budget, so it is surfaced on the first attempt.
                            if (!IsRetryableError(transportFailure.ErrorCode))
                            {
                                yield return transportFailure;
                                yield break;
                            }

                            retryablePreCommitFailure = true;
                            terminalErrorChunk = transportFailure;
                            break;
                        }

                        if (!hasNext)
                        {
                            retryablePreCommitFailure = true;
                            terminalErrorChunk = new LlmStreamChunk
                            {
                                IsDone = true,
                                Error = "stream ended without content",
                                ErrorCode = LlmErrorCode.EmptyResponse
                            };
                            break;
                        }

                        if (!string.IsNullOrEmpty(current.Error))
                        {
                            if (IsRetryableError(current.ErrorCode))
                            {
                                retryablePreCommitFailure = true;
                                terminalErrorChunk = current;
                                break;
                            }

                            yield return current;
                            yield break;
                        }

                        if (IsCommittingChunk(current))
                        {
                            yield return current;
                            committed = true;
                            break;
                        }

                        if (current.IsDone)
                        {
                            retryablePreCommitFailure = true;
                            terminalErrorChunk = new LlmStreamChunk
                            {
                                IsDone = true,
                                Error = "stream ended without content",
                                ErrorCode = LlmErrorCode.EmptyResponse
                            };
                            break;
                        }

                        // WHY: Benign pre-commit control/hint chunk (no text/tool/error, not done): forward it and
                        // keep watching under the same retry protection.
                        yield return current;
                    }

                    if (committed)
                    {
                        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            yield return enumerator.Current;
                        }

                        yield break;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                if (!retryablePreCommitFailure)
                {
                    yield break;
                }

                if (attempt >= _maxRetryAttempts)
                {
                    yield return terminalErrorChunk ?? new LlmStreamChunk
                    {
                        IsDone = true,
                        Error = "stream failed after retries",
                        ErrorCode = LlmErrorCode.ProviderError
                    };
                    yield break;
                }

                RetryCount++;
                _log?.Invoke(
                    $"[StreamRetry] pre-commit failure ({terminalErrorChunk?.ErrorCode}), retry {attempt + 1}/{_maxRetryAttempts}");

                if (_retryDelay != null)
                {
                    TimeSpan delay = _retryDelay(attempt);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private static bool IsCommittingChunk(LlmStreamChunk chunk)
        {
            return !string.IsNullOrEmpty(chunk.Text) ||
                   (chunk.ExecutedToolCalls != null && chunk.ExecutedToolCalls.Count > 0);
        }

        /// <summary>
        /// Turns a mid-stream transport fault into the terminal chunk the caller would have received.
        /// <para>
        /// The typed <see cref="LlmClientException"/> already carries the adapter's classification
        /// (HTTP status, error code, retry hint). Flattening every exception to
        /// <see cref="LlmErrorCode.ProviderError"/> — as this decorator used to — erased that: an
        /// HTTP 402/401/400 arrived here already classified as permanent and was retried anyway,
        /// because ProviderError is on the retryable list.
        /// </para>
        /// </summary>
        private static LlmStreamChunk DescribeTransportFailure(Exception ex)
        {
            LlmClientException typed = ex as LlmClientException;
            return new LlmStreamChunk
            {
                IsDone = true,
                Error = ex.Message,
                ErrorCode = typed?.ErrorCode ?? LlmErrorCode.ProviderError,
                HttpStatus = typed?.HttpStatus,
                RetryAfterSeconds = typed?.RetryAfterSeconds
            };
        }

        /// <summary>
        /// Failure categories worth re-opening the SAME stream for. It is a whitelist on purpose: an
        /// unlisted (or newly added) code is never retried, which is what keeps permanent refusals —
        /// <see cref="LlmErrorCode.PaymentRequired"/>, <see cref="LlmErrorCode.AuthExpired"/>,
        /// <see cref="LlmErrorCode.InvalidRequest"/>,
        /// <see cref="LlmErrorCode.PermanentProviderError"/> — from burning the whole retry budget.
        /// </summary>
        private static bool IsRetryableError(LlmErrorCode code)
        {
            return code == LlmErrorCode.ProviderError ||
                   code == LlmErrorCode.BackendUnavailable ||
                   code == LlmErrorCode.RateLimited ||
                   code == LlmErrorCode.Timeout ||
                   code == LlmErrorCode.EmptyResponse;
        }
    }
}
#endif
