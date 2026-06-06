using System;
using System.Threading;

namespace CoreAI.Ai
{
    /// <summary>
    /// Per-request ambient context bridging the orchestrator/<see cref="LlmCompletionRequest"/>
    /// layer to HTTP transports. Carries the role id, end-to-end trace id, and idempotency key
    /// so backends can attribute requests and safely deduplicate retries without the client
    /// re-issuing logical work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it relates to <see cref="LlmCompletionRequest.IdempotencyKey"/> and
    /// <see cref="IRequestHeaderProvider"/>:</b> they cover three layers and never conflict.
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="LlmCompletionRequest.IdempotencyKey"/> belongs to the logical request
    /// owned by the orchestrator. Auto-assigned once when empty and reused across decorator
    /// retries (e.g. <c>RefreshOnUnauthorizedDecorator</c>). The MEAI host (<c>MeaiLlmClient</c>)
    /// copies this value into <see cref="Begin"/> on every <c>CompleteAsync</c>/<c>CompleteStreamingAsync</c>
    /// invocation.</description></item>
    /// <item><description><see cref="LlmRequestContext"/> is the ambient bridge used by the HTTP
    /// transport without plumbing the request object through MEAI's <c>IChatClient</c> seam.
    /// Implemented as <see cref="AsyncLocal{T}"/> so it follows <c>await</c> continuations across
    /// thread-pool hops.</description></item>
    /// <item><description><see cref="IRequestHeaderProvider"/> contributes
    /// <b>per settings</b> static/host-supplied headers (e.g. <c>X-Client-Version</c>, custom routing
    /// hints). When this provider also exposes <c>IdempotencyKey</c>/<c>RequestId</c> they act only as
    /// fallbacks for callers using <see cref="MeaiOpenAiChatClient"/> directly without an orchestrator.</description></item>
    /// </list>
    /// <para>
    /// Resolution order in <c>MeaiOpenAiChatClient.BuildTransportHeaders</c>:
    /// request context first, then host-provided headers, then settings fallbacks.
    /// Earlier sources win; later sources fill missing slots only.
    /// </para>
    /// <para>
    /// Always wrap modifications in a <see cref="Scope"/> to restore the previous frame on
    /// dispose; nested calls are supported.
    /// </para>
    /// </remarks>
    public static class LlmRequestContext
    {
        private static readonly AsyncLocal<LlmRequestContextFrame> _current = new();

        /// <summary>Current frame or <c>null</c> when no request is in flight.</summary>
        public static LlmRequestContextFrame Current => _current.Value;

        /// <summary>Pushes a new frame; the returned <see cref="IDisposable"/> restores the prior frame.</summary>
        public static Scope Begin(string agentRoleId, string traceId, string idempotencyKey)
        {
            LlmRequestContextFrame previous = _current.Value;
            _current.Value = new LlmRequestContextFrame(
                agentRoleId ?? "",
                string.IsNullOrEmpty(traceId) ? Guid.NewGuid().ToString("N") : traceId,
                string.IsNullOrEmpty(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey);
            return new Scope(previous);
        }

        /// <summary>RAII guard used by <see cref="Begin"/>.</summary>
        public readonly struct Scope : IDisposable
        {
            private readonly LlmRequestContextFrame _previous;

            internal Scope(LlmRequestContextFrame previous)
            {
                _previous = previous;
            }

            /// <summary>Restores the prior frame.</summary>
            public void Dispose()
            {
                _current.Value = _previous;
            }
        }
    }

    /// <summary>One LLM-request frame: role, trace, idempotency key.</summary>
    public sealed class LlmRequestContextFrame
    {
        /// <summary>Agent role id (e.g. <c>SmartChat</c>, <c>Teacher</c>).</summary>
        public string AgentRoleId { get; }

        /// <summary>End-to-end trace id; matches <see cref="LlmCompletionRequest.TraceId"/>.</summary>
        public string TraceId { get; }

        /// <summary>Idempotency key sent as <c>Idempotency-Key</c> on each retry of the same logical request.</summary>
        public string IdempotencyKey { get; }

        /// <summary>Constructs a frame.</summary>
        public LlmRequestContextFrame(string agentRoleId, string traceId, string idempotencyKey)
        {
            AgentRoleId = agentRoleId ?? "";
            TraceId = traceId ?? "";
            IdempotencyKey = idempotencyKey ?? "";
        }
    }
}