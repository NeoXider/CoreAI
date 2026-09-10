using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Local client-side limits applied before reaching the inner LLM client: a per-session request cap
    /// and a prompt size cap.
    /// <para>
    /// Both refusals are <see cref="LlmErrorCode.ClientLimitExceeded"/>: this is the CLIENT's own decision,
    /// the backend was never asked. <see cref="LlmErrorCode.QuotaExceeded"/> used to be returned instead,
    /// and the presentation told the player "your account quota is exhausted" when all that ran out was a
    /// local session counter.
    /// </para>
    /// <para>
    /// A session slot is reserved for the duration of the request and GIVEN BACK if the request failed
    /// (an error result, a terminal error chunk, an exception, cancellation): an unsuccessful attempt does
    /// not eat the limit. It is a reservation rather than an after-the-fact count so that concurrent
    /// requests cannot slip past the cap two at a time. A stream the consumer abandoned after part of the
    /// answer keeps the slot: the backend did the work. The counter lives exactly as long as the instance
    /// does - that is what "session" means here.
    /// </para>
    /// </summary>
    public sealed class ClientLimitedLlmClientDecorator : ILlmClient
    {
        /// <summary>Refusal text for an exhausted session request cap (diagnostics, not player-facing copy).</summary>
        public const string RequestLimitError = "ClientLimited request limit exceeded";

        /// <summary>Refusal text for an exceeded prompt size cap (diagnostics, not player-facing copy).</summary>
        public const string PromptLimitError = "ClientLimited prompt character limit exceeded";

        /// <summary>Exception.Data key containing a secondary stream cleanup exception when the request already failed.</summary>
        public const string StreamDisposeExceptionDataKey = "CoreAI.StreamDisposeException";

        private readonly ILlmClient _inner;
        private readonly int _maxRequestsPerSession;
        private readonly int _maxPromptChars;
        private int _reservedRequests;

        /// <summary>
        /// Creates a local client-side limiter around a single permitted LLM client.
        /// </summary>
        public ClientLimitedLlmClientDecorator(ILlmClient inner, int maxRequestsPerSession, int maxPromptChars)
        {
            _inner = inner ?? new StubLlmClient();
            _maxRequestsPerSession = maxRequestsPerSession < 0 ? 0 : maxRequestsPerSession;
            _maxPromptChars = maxPromptChars < 0 ? 0 : maxPromptChars;
        }

        /// <summary>
        /// The wrapped client that requests passing the local limits are forwarded to.
        /// </summary>
        public ILlmClient Inner => _inner;

        /// <inheritdoc />
        public bool SupportsNativeToolCalling => _inner?.SupportsNativeToolCalling == true;

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return _inner?.SupportsNativeToolCallingForRole(agentRoleId) == true;
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCallingForRole(string agentRoleId, string routingProfileId)
        {
            return _inner?.SupportsNativeToolCallingForRole(agentRoleId, routingProfileId) == true;
        }

        /// <inheritdoc />
        public int? ResolveContextWindowTokensForRole(string agentRoleId, string routingProfileId)
        {
            return _inner?.ResolveContextWindowTokensForRole(agentRoleId, routingProfileId);
        }

        /// <inheritdoc />
        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _inner?.SetTools(tools);
        }

        /// <summary>
        /// Checks the local limits and delegates the non-streaming request.
        /// </summary>
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            string rejection = TryReserve(request);
            if (rejection != null)
            {
                return Rejected(rejection);
            }

            bool succeeded = false;
            try
            {
                // WHY no ConfigureAwait(false): WebGL has no thread pool, so a continuation marked
                // non-inlinable under Unity's SynchronizationContext is posted nowhere and never resumes —
                // a silent permanent hang, not an exception. Staying on the captured context is safe here.
                LlmCompletionResult result = await _inner.CompleteAsync(request, cancellationToken);
                succeeded = result != null && result.Ok;
                return result;
            }
            finally
            {
                if (!succeeded)
                {
                    ReleaseReservation();
                }
            }
        }

        /// <summary>
        /// Checks the local limits and delegates the streaming request.
        /// </summary>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            string rejection = TryReserve(request);
            if (rejection != null)
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = rejection,
                    ErrorCode = LlmErrorCode.ClientLimitExceeded
                };
                yield break;
            }

            bool failed = false;
            bool sawAnyChunk = false;
            Exception primaryFailure = null;
            IAsyncEnumerator<LlmStreamChunk> enumerator = null;
            try
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    enumerator = _inner.CompleteStreamingAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                }
                catch (Exception exception) { failed = true; primaryFailure = exception; throw; }
                while (true)
                {
                    bool hasNext;
                    LlmStreamChunk chunk;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        hasNext = await enumerator.MoveNextAsync();
                        cancellationToken.ThrowIfCancellationRequested();
                        chunk = hasNext ? enumerator.Current : null;
                    }
                    catch (Exception exception) { failed = true; primaryFailure = exception; throw; }
                    if (!hasNext) break;
                    if (chunk != null)
                    {
                        sawAnyChunk = true;
                        if (!string.IsNullOrEmpty(chunk.Error) || chunk.ErrorCode != LlmErrorCode.None) failed = true;
                    }
                    yield return chunk;
                }
            }
            finally
            {
                try
                {
                    if (enumerator != null) await enumerator.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failed = true;
                    // WHY: cleanup diagnostics must not replace the provider failure used by auth/retry classification.
                    if (primaryFailure == null) throw;
                    primaryFailure.Data[StreamDisposeExceptionDataKey] = exception;
                }
                finally
                {
                    // WHY: an actual stream/cleanup failure refunds once; abandoning a partial answer retains the slot.
                    if (failed || !sawAnyChunk || cancellationToken.IsCancellationRequested) ReleaseReservation();
                }
            }
        }

        /// <summary>The refusal reason, or null once a slot is reserved (or the caps are disabled).</summary>
        private string TryReserve(LlmCompletionRequest request)
        {
            if (_maxPromptChars > 0 && EstimatePromptChars(request) > _maxPromptChars)
            {
                return PromptLimitError;
            }

            if (_maxRequestsPerSession <= 0)
            {
                return null;
            }

            int reserved = Interlocked.Increment(ref _reservedRequests);
            if (reserved > _maxRequestsPerSession)
            {
                Interlocked.Decrement(ref _reservedRequests);
                return RequestLimitError;
            }

            return null;
        }

        private void ReleaseReservation()
        {
            if (_maxRequestsPerSession > 0)
            {
                Interlocked.Decrement(ref _reservedRequests);
            }
        }

        private static LlmCompletionResult Rejected(string error)
        {
            return new LlmCompletionResult
            {
                Ok = false,
                Error = error,
                ErrorCode = LlmErrorCode.ClientLimitExceeded
            };
        }

        private static int EstimatePromptChars(LlmCompletionRequest request)
        {
            if (request == null)
            {
                return 0;
            }

            int total = (request.SystemPrompt?.Length ?? 0) + (request.UserPayload?.Length ?? 0);
            if (request.ChatHistory == null)
            {
                return total;
            }

            foreach (Microsoft.Extensions.AI.ChatMessage message in request.ChatHistory)
            {
                total += message.Text?.Length ?? 0;
            }

            return total;
        }
    }
}
