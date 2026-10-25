#if COREAI_LLM
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
    /// <see cref="GlobalMessagePipe"/> so UI can prompt for re-login. A turn that has already
    /// executed a tool or emitted visible streaming output is never replayed.
    /// </summary>
    public sealed class RefreshOnUnauthorizedDecorator : ILlmClient, ILlmRequestHeaderScope
    {
        private readonly ILlmClient _inner;

        /// <summary>Wraps <paramref name="inner"/>. Pass <c>null</c> for tests where stub fallback is acceptable.</summary>
        public RefreshOnUnauthorizedDecorator(ILlmClient inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc />
        public void ResolveRequestSettings(LlmCompletionRequest request)
        {
            _inner.ResolveRequestSettings(request);
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCalling => _inner.SupportsNativeToolCalling;

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return _inner.SupportsNativeToolCallingForRole(agentRoleId);
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            using IDisposable requestHeaders = BeginRequestHeaders(request);
            LlmCompletionResult result;
            try
            {
                result = await _inner.CompleteAsync(request, cancellationToken);
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

            cancellationToken.ThrowIfCancellationRequested();
            bool refreshed = await TryRefreshAsync(cancellationToken);
            if (!refreshed)
            {
                PublishExpiredEvent(request);
                return result;
            }

            if (LoggingLlmClientDecorator.HasInvokedToolCalls(result.ExecutedToolCalls) ||
                request?.ProviderResponseState?.RetrySuppressed == true)
            {
                return result;
            }

            return await _inner.CompleteAsync(request, cancellationToken);
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            using IDisposable requestHeaders = BeginRequestHeaders(request);
            LlmStreamChunk authFailure = null;
            bool committed = false;
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
                        hasNext = await enumerator.MoveNextAsync();
                        current = hasNext ? enumerator.Current : null;
                    }
                    catch (LlmClientException ex) when (ex.ErrorCode == LlmErrorCode.AuthExpired)
                    {
                        authFailure = new LlmStreamChunk
                        {
                            IsDone = true,
                            Error = ex.Message,
                            ErrorCode = ex.ErrorCode,
                            HttpStatus = ex.HttpStatus,
                            RetryAfterSeconds = ex.RetryAfterSeconds
                        };
                        hasNext = false;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    if (current == null)
                    {
                        continue;
                    }

                    committed |= !string.IsNullOrEmpty(current.Text) ||
                                 (current.ExecutedToolCalls != null && current.ExecutedToolCalls.Count > 0);
                    if (current.ErrorCode == LlmErrorCode.AuthExpired)
                    {
                        authFailure = current;
                        break;
                    }

                    yield return current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (authFailure == null)
            {
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            bool refreshed = await TryRefreshAsync(cancellationToken);
            if (!refreshed || committed || request?.ProviderResponseState?.RetrySuppressed == true)
            {
                if (!refreshed)
                {
                    PublishExpiredEvent(request);
                }

                yield return authFailure;
                yield break;
            }

            await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, cancellationToken))
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
                return await refresher.RefreshAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private IDisposable BeginRequestHeaders(LlmCompletionRequest request)
        {
            return LlmRequestHeaderScopes.Begin(_inner, request);
        }

        IDisposable ILlmRequestHeaderScope.BeginRequestHeaders(LlmCompletionRequest request)
        {
            return BeginRequestHeaders(request);
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
