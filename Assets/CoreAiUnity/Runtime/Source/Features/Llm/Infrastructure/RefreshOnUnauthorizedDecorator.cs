#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Messaging;
using MessagePipe;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Wraps an <see cref="ILlmClient"/> so that a single <see cref="LlmErrorCode.AuthExpired"/>
    /// failure triggers <see cref="ServerManagedAuthorization.Refresher"/> and one retry.
    /// When refresh is missing or unsuccessful, publishes <see cref="LlmAuthExpired"/> on
    /// <see cref="GlobalMessagePipe"/> so UI can prompt for re-login. Idempotency guarantees
    /// hold because the inner client preserves the same <c>Idempotency-Key</c> across the
    /// retry (header derived from <see cref="LlmRequestContextFrame.IdempotencyKey"/>).
    /// </summary>
    public sealed class RefreshOnUnauthorizedDecorator : ILlmClient
    {
        private readonly ILlmClient _inner;

        /// <summary>Wraps <paramref name="inner"/>. Pass <c>null</c> for tests where stub fallback is acceptable.</summary>
        public RefreshOnUnauthorizedDecorator(ILlmClient inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            LlmCompletionResult result;
            try
            {
                result = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (LlmClientException ex) when (ex.ErrorCode == LlmErrorCode.AuthExpired)
            {
                result = new LlmCompletionResult
                {
                    Ok = false,
                    Error = ex.Message,
                    ErrorCode = LlmErrorCode.AuthExpired,
                    HttpStatus = ex.HttpStatus,
                    RetryAfterSeconds = ex.RetryAfterSeconds,
                    ProviderErrorBody = ex.ProviderErrorBody
                };
            }

            if (result == null || result.Ok || result.ErrorCode != LlmErrorCode.AuthExpired)
            {
                return result;
            }

            bool refreshed = await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed)
            {
                PublishExpiredEvent(request);
                return result;
            }

            return await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            bool authExpired = false;
            bool emittedVisibleText = false;
            IAsyncEnumerator<LlmStreamChunk> enumerator =
                _inner.CompleteStreamingAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);

            try
            {
                while (true)
                {
                    bool hasNext;
                    LlmStreamChunk current = null;
                    try
                    {
                        // No ConfigureAwait(false): WebGL has no working ThreadPool, and the
                        // continuation must come back through UnitySynchronizationContext.
                        hasNext = await enumerator.MoveNextAsync();
                        current = hasNext ? enumerator.Current : null;
                    }
                    catch (LlmClientException ex) when (ex.ErrorCode == LlmErrorCode.AuthExpired)
                    {
                        authExpired = true;
                        hasNext = false;
                    }

                    if (!hasNext)
                    {
                        if (current != null && current.IsDone && current.ErrorCode == LlmErrorCode.AuthExpired)
                        {
                            authExpired = true;
                        }
                        else if (current != null)
                        {
                            if (!string.IsNullOrEmpty(current.Text))
                            {
                                emittedVisibleText = true;
                            }

                            yield return current;
                        }

                        break;
                    }

                    if (current != null && current.IsDone && current.ErrorCode == LlmErrorCode.AuthExpired)
                    {
                        authExpired = true;
                        break;
                    }

                    if (!string.IsNullOrEmpty(current?.Text))
                    {
                        emittedVisibleText = true;
                    }

                    yield return current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (!authExpired)
            {
                yield break;
            }

            bool refreshed = await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed || emittedVisibleText)
            {
                if (!refreshed)
                {
                    PublishExpiredEvent(request);
                }

                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = "Auth expired",
                    ErrorCode = LlmErrorCode.AuthExpired
                };
                yield break;
            }

            await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return chunk;
            }
        }

        private static async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
        {
            IServerManagedAuthRefresher refresher = ServerManagedAuthorization.Refresher;
            if (refresher == null)
            {
                return false;
            }

            try
            {
                return await refresher.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        private static void PublishExpiredEvent(LlmCompletionRequest request)
        {
            try
            {
                IPublisher<LlmAuthExpired> pub = GlobalMessagePipe.GetPublisher<LlmAuthExpired>();
                pub?.Publish(new LlmAuthExpired(
                    request?.TraceId ?? "",
                    request?.AgentRoleId ?? "",
                    ServerManagedAuthorization.Refresher != null,
                    false));
            }
            catch
            {
            }
        }
    }
}
#endif
