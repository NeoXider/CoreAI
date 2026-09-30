using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

[assembly: InternalsVisibleTo("CoreAI.Tests.PlayMode.FastNoLlm")]
[assembly: InternalsVisibleTo("CoreAI.Tests.PlayMode.LlmVerification")]

namespace CoreAI.Tests.PlayMode
{
    /// <summary>Requests currently inside the counting clients that share it, and the highest count reached.</summary>
    internal sealed class InFlightMeter
    {
        private int _current;
        private int _peak;

        public int Current => Volatile.Read(ref _current);

        public int Peak => Volatile.Read(ref _peak);

        public void Enter()
        {
            int now = Interlocked.Increment(ref _current);
            int seen = Volatile.Read(ref _peak);
            while (now > seen)
            {
                int previous = Interlocked.CompareExchange(ref _peak, now, seen);
                if (previous == seen)
                {
                    break;
                }

                seen = previous;
            }
        }

        public void Exit()
        {
            Interlocked.Decrement(ref _current);
        }
    }

    /// <summary>
    /// Counts a request as in flight at the <see cref="ILlmClient"/> boundary: from the
    /// <see cref="CompleteAsync"/> call until the inner completion returns, and for a stream from the first
    /// <c>MoveNextAsync</c> (an async iterator runs no code before it) until the inner stream ends or the
    /// enumerator is disposed. It wraps the client from outside, so it cannot see whether the requests also
    /// overlap inside the client or on the wire.
    /// <para>
    /// Forwards every public <see cref="ILlmClient"/> member; both completion paths are overridden because the
    /// orchestrator streams by default and the interface's default streaming body would bypass the inner
    /// client's stream. The internal request-header scope of CoreAI.Core cannot be forwarded from a test
    /// assembly, so do not wrap a client that relies on it.
    /// </para>
    /// </summary>
    internal sealed class InFlightCountingLlmClient : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly InFlightMeter _meter;

        public InFlightCountingLlmClient(ILlmClient inner, InFlightMeter meter)
        {
            _inner = inner;
            _meter = meter;
        }

        public bool SupportsNativeToolCalling => _inner.SupportsNativeToolCalling;

        public bool SupportsNativeToolCallingForRole(string agentRoleId)
        {
            return _inner.SupportsNativeToolCallingForRole(agentRoleId);
        }

        public bool SupportsNativeToolCallingForRole(string agentRoleId, string routingProfileId)
        {
            return _inner.SupportsNativeToolCallingForRole(agentRoleId, routingProfileId);
        }

        public int? ResolveContextWindowTokensForRole(string agentRoleId, string routingProfileId)
        {
            return _inner.ResolveContextWindowTokensForRole(agentRoleId, routingProfileId);
        }

        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _inner.SetTools(tools);
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            _meter.Enter();
            try
            {
                return await _inner.CompleteAsync(request, cancellationToken);
            }
            finally
            {
                _meter.Exit();
            }
        }

        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _meter.Enter();
            try
            {
                await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, cancellationToken))
                {
                    yield return chunk;
                }
            }
            finally
            {
                _meter.Exit();
            }
        }
    }
}
