using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Applies local client-side request and prompt limits before delegating to an inner LLM client.
    /// </summary>
    public sealed class ClientLimitedLlmClientDecorator : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly int _maxRequestsPerSession;
        private readonly int _maxPromptChars;
        private int _requestCount;

        /// <summary>
        /// Creates a local client-side limiter for one resolved LLM client.
        /// </summary>
        public ClientLimitedLlmClientDecorator(ILlmClient inner, int maxRequestsPerSession, int maxPromptChars)
        {
            _inner = inner ?? new StubLlmClient();
            _maxRequestsPerSession = maxRequestsPerSession < 0 ? 0 : maxRequestsPerSession;
            _maxPromptChars = maxPromptChars < 0 ? 0 : maxPromptChars;
        }

        /// <summary>
        /// Wrapped client used after local limits pass.
        /// </summary>
        public ILlmClient Inner => _inner;

        /// <summary>
        /// Checks local limits and delegates a non-streaming request.
        /// </summary>
        public Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            string error = TryConsume(request);
            if (!string.IsNullOrEmpty(error))
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = false,
                    Error = error,
                    ErrorCode = LlmErrorCode.QuotaExceeded
                });
            }

            return _inner.CompleteAsync(request, cancellationToken);
        }

        /// <summary>
        /// Checks local limits and delegates a streaming request.
        /// </summary>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string error = TryConsume(request);
            if (!string.IsNullOrEmpty(error))
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = error,
                    ErrorCode = LlmErrorCode.QuotaExceeded
                };
                yield break;
            }

            await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, cancellationToken))
            {
                yield return chunk;
            }
        }

        private string TryConsume(LlmCompletionRequest request)
        {
            if (_maxPromptChars > 0 && EstimatePromptChars(request) > _maxPromptChars)
            {
                return "ClientLimited prompt character limit exceeded";
            }

            if (_maxRequestsPerSession > 0 && Interlocked.Increment(ref _requestCount) > _maxRequestsPerSession)
            {
                return "ClientLimited request limit exceeded";
            }

            return "";
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
