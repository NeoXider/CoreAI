using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Wraps a primary and secondary <see cref="ILlmClient"/>. When the primary fails
    /// (non-cancellation error), the request is automatically retried on the secondary backend.
    /// </summary>
    public sealed class FallbackLlmClientDecorator : ILlmClient
    {
        private readonly ILlmClient _primary;
        private readonly ILlmClient _secondary;
        private readonly ILog _logger;

        /// <summary>Total number of times the secondary backend was invoked due to primary failures.</summary>
        public int FallbackCount { get; private set; }

        public FallbackLlmClientDecorator(ILlmClient primary, ILlmClient secondary, ILog logger = null)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
            _logger = logger ?? NullLog.Instance;
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                LlmCompletionResult result = await _primary.CompleteAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                // If primary returned a result but with a retryable error, try secondary
                if (result != null && !result.Ok && IsRetryableError(result.ErrorCode))
                {
                    _logger.Warn(
                        $"[Fallback] Primary failed ({result.ErrorCode}: {result.Error}), falling back to secondary.",
                        LogTag.Llm);
                    FallbackCount++;
                    return await _secondary.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw; // Never fallback on user cancellation
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    $"[Fallback] Primary threw {ex.GetType().Name}: {ex.Message}, falling back to secondary.",
                    LogTag.Llm);
                FallbackCount++;
                return await _secondary.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            bool primaryFailed = false;

            IAsyncEnumerator<LlmStreamChunk> enumerator = null;
            try
            {
                enumerator = _primary.CompleteStreamingAsync(request, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

                // Try to get the first chunk — this is where most connection errors surface
                bool hasFirst;
                try
                {
                    hasFirst = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn(
                        $"[Fallback] Primary streaming threw {ex.GetType().Name}: {ex.Message}, falling back to secondary.",
                        LogTag.Llm);
                    primaryFailed = true;
                    hasFirst = false;
                }

                if (!primaryFailed && hasFirst)
                {
                    // First chunk from primary — check if it's an error
                    LlmStreamChunk first = enumerator.Current;
                    if (!string.IsNullOrEmpty(first.Error) && IsRetryableError(first.ErrorCode))
                    {
                        _logger.Warn(
                            $"[Fallback] Primary streaming error chunk ({first.ErrorCode}), falling back to secondary.",
                            LogTag.Llm);
                        primaryFailed = true;
                    }
                    else
                    {
                        yield return first;

                        // Continue streaming from primary
                        while (await enumerator.MoveNextAsync())
                        {
                            yield return enumerator.Current;
                        }

                        yield break;
                    }
                }
            }
            finally
            {
                if (enumerator != null)
                {
                    await enumerator.DisposeAsync();
                }
            }

            // Fallback to secondary
            if (primaryFailed)
            {
                FallbackCount++;
                await foreach (LlmStreamChunk chunk in _secondary.CompleteStreamingAsync(request, cancellationToken))
                {
                    yield return chunk;
                }
            }
        }

        private static bool IsRetryableError(LlmErrorCode code)
        {
            return code == LlmErrorCode.ProviderError ||
                   code == LlmErrorCode.BackendUnavailable ||
                   code == LlmErrorCode.RateLimited ||
                   code == LlmErrorCode.Timeout ||
                   code == LlmErrorCode.ContextLengthExceeded;
        }
    }
}
