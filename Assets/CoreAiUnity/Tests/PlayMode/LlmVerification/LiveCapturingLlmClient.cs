using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Pass-through <see cref="ILlmClient"/> for live fixtures that records the last request and how the last
    /// completion ended, so a gate can report why a turn failed even when the orchestrator swallowed the
    /// client's exception.
    /// <para>
    /// Only <see cref="CompleteAsync"/> and <see cref="SetTools"/> are forwarded: streaming falls back to the
    /// interface default over <see cref="CompleteAsync"/>, and the native tool-calling flags keep their
    /// interface defaults, which is the behaviour the live fixtures using it were written against.
    /// </para>
    /// </summary>
    internal sealed class LiveCapturingLlmClient : ILlmClient
    {
        // TODO: decide whether to forward SupportsNativeToolCalling*/ResolveContextWindowTokensForRole; the
        // defaults make the orchestrator add the text tool contract even when the inner client calls tools
        // natively, which differs from production decorators that forward them.
        private readonly ILlmClient _inner;

        public LiveCapturingLlmClient(ILlmClient inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public string LastSystemPrompt { get; private set; }

        public string LastUserPayload { get; private set; }

        public string LastRoleId { get; private set; }

        public IList<Microsoft.Extensions.AI.ChatMessage> LastChatHistory { get; private set; }

        /// <summary>The last completion the inner client returned; <c>null</c> when it threw or never ran.</summary>
        public LlmCompletionResult LastResult { get; private set; }

        /// <summary>The exception the inner client threw on the last call, if any.</summary>
        public Exception LastException { get; private set; }

        /// <summary>
        /// True for a successful turn and for one whose only failure is a missing closing text
        /// (<see cref="LlmErrorCode.EmptyResponse"/>). No captured completion - e.g. the inner client threw
        /// and the orchestrator swallowed it - is a failure.
        /// </summary>
        public bool LastIsOkOrEmptyFinalText =>
            LastResult != null && (LastResult.Ok || LastResult.ErrorCode == LlmErrorCode.EmptyResponse);

        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSystemPrompt = request?.SystemPrompt;
            LastUserPayload = request?.UserPayload;
            LastRoleId = request?.AgentRoleId;
            LastChatHistory = request?.ChatHistory;

            LastResult = null;
            LastException = null;
            try
            {
                LastResult = await _inner.CompleteAsync(request, cancellationToken);
                return LastResult;
            }
            catch (Exception ex)
            {
                // WHY: the orchestrator may swallow this, so keep it for the gate's failure message.
                LastException = ex;
                throw;
            }
        }

        /// <summary>How the last completion ended, for assertion messages.</summary>
        public string DescribeLastOutcome()
        {
            if (LastResult != null)
            {
                return $"{LastResult.ErrorCode} {LastResult.Error}";
            }

            return LastException != null
                ? $"the client threw {LastException.GetBaseException().Message}"
                : "no completion was captured";
        }

        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _inner.SetTools(tools);
        }
    }
}
