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
        public bool SupportsNativeToolCalling =>
            _primary.SupportsNativeToolCalling && _secondary.SupportsNativeToolCalling;

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return _primary.SupportsNativeToolCallingForRole(agentRoleId) &&
                   _secondary.SupportsNativeToolCallingForRole(agentRoleId);
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                LlmCompletionResult result = await _primary.CompleteAsync(request, cancellationToken);

                // WHY: If primary returned a result but with a retryable error, try secondary
                if (result != null &&
                    !result.Ok &&
                    !HasExecutedToolCalls(result) &&
                    IsRetryableError(result.ErrorCode))
                {
                    _logger.Warn(
                        $"[Fallback] Primary failed ({result.ErrorCode}: {result.Error}), falling back to secondary.",
                        LogTag.Llm);
                    FallbackCount++;
                    return await _secondary.CompleteAsync(request, cancellationToken);
                }

                return result;
            }
            // WHY: The caller's own token was cancelled: this is a genuine user-initiated stop, never fall back.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // WHY: OperationCanceledException with an un-cancelled caller token is an internal provider/transport
            // timeout (e.g. MeaiOpenAiChatClient's transport-level timeout), not a user cancellation.
            catch (OperationCanceledException ex)
            {
                _logger.Warn(
                    $"[Fallback] Primary timed out internally ({ex.GetType().Name}), falling back to secondary.",
                    LogTag.Llm);
                FallbackCount++;
                return await _secondary.CompleteAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    $"[Fallback] Primary threw {ex.GetType().Name}: {ex.Message}, falling back to secondary.",
                    LogTag.Llm);
                FallbackCount++;
                return await _secondary.CompleteAsync(request, cancellationToken);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// The primary stream is "committed" once it produces a chunk with visible text, a tool call,
        /// or a forwarded (non-retryable) error - at that point falling back would duplicate output or
        /// re-run tool side effects, so failures after commitment simply propagate. Before commitment
        /// (e.g. a tool-buffering control chunk with no text/tool content), a timeout or failure on any
        /// subsequent <c>MoveNextAsync</c> is still safe to fall back from and restarts the stream
        /// cleanly on the secondary backend.
        /// </remarks>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            bool primaryFailed = false;
            bool committed = false;

            IAsyncEnumerator<LlmStreamChunk> enumerator = null;
            try
            {
                enumerator = _primary.CompleteStreamingAsync(request, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

                while (!committed)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    // WHY: The caller's own token was cancelled: never fall back.
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException ex)
                    {
                        _logger.Warn(
                            $"[Fallback] Primary streaming timed out internally before committing any content " +
                            $"({ex.GetType().Name}), falling back to secondary.",
                            LogTag.Llm);
                        primaryFailed = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(
                            $"[Fallback] Primary streaming threw {ex.GetType().Name}: {ex.Message}, falling back to secondary.",
                            LogTag.Llm);
                        primaryFailed = true;
                        break;
                    }

                    if (!hasNext)
                    {
                        _logger.Warn(
                            "[Fallback] Primary streaming completed without committing any content, falling back to secondary.",
                            LogTag.Llm);
                        primaryFailed = true;
                        break;
                    }

                    LlmStreamChunk chunk = enumerator.Current;
                    if (!string.IsNullOrEmpty(chunk.Error) && IsRetryableError(chunk.ErrorCode))
                    {
                        _logger.Warn(
                            $"[Fallback] Primary streaming error chunk ({chunk.ErrorCode}), falling back to secondary.",
                            LogTag.Llm);
                        primaryFailed = true;
                        break;
                    }

                    if (IsCommittingChunk(chunk))
                    {
                        yield return chunk;
                        committed = true;
                        break;
                    }

                    if (chunk.IsDone)
                    {
                        // WHY: Terminal chunk with no visible text/tool call/error: nothing was ever committed,
                        // so discard it and restart cleanly on the secondary instead of forwarding an empty end.
                        _logger.Warn(
                            "[Fallback] Primary streaming ended without committing any content, falling back to secondary.",
                            LogTag.Llm);
                        primaryFailed = true;
                        break;
                    }

                    // WHY: Benign pre-commitment control/hint chunk (no text, no tool call, not done): forward it
                    // and keep watching subsequent MoveNextAsync calls under the same fallback protection.
                    yield return chunk;
                }

                if (committed)
                {
                    // WHY: Already streamed real content: continuing failures are no longer safe to fall back
                    // from (would duplicate output or re-run tool side effects), so let them propagate.
                    while (await enumerator.MoveNextAsync())
                    {
                        yield return enumerator.Current;
                    }

                    yield break;
                }
            }
            finally
            {
                if (enumerator != null)
                {
                    await enumerator.DisposeAsync();
                }
            }

            // WHY: Fallback to secondary
            if (primaryFailed)
            {
                FallbackCount++;
                await foreach (LlmStreamChunk chunk in _secondary.CompleteStreamingAsync(request, cancellationToken))
                {
                    yield return chunk;
                }
            }
        }

        private static bool IsCommittingChunk(LlmStreamChunk chunk)
        {
            return !string.IsNullOrEmpty(chunk.Text) ||
                   !string.IsNullOrEmpty(chunk.Error) ||
                   (chunk.ExecutedToolCalls != null && chunk.ExecutedToolCalls.Count > 0);
        }

        private static bool IsRetryableError(LlmErrorCode code)
        {
            return code == LlmErrorCode.ProviderError ||
                   code == LlmErrorCode.BackendUnavailable ||
                   code == LlmErrorCode.RateLimited ||
                   code == LlmErrorCode.Timeout ||
                   code == LlmErrorCode.ContextLengthExceeded;
        }

        /// <summary>
        /// Fallback is suppressed only when a tool body actually ran (it may have mutated state).
        /// Rejected/never-invoked traces (duplicate, parse error, unknown/missing tool) executed
        /// nothing, so a retryable primary failure carrying only those must still fall back.
        /// </summary>
        private static bool HasExecutedToolCalls(LlmCompletionResult result)
        {
            return LoggingLlmClientDecorator.HasInvokedToolCalls(result?.ExecutedToolCalls);
        }
    }
}
