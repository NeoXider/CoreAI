#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// A circuit breaker for <see cref="ILlmClient"/>. After <c>failureThreshold</c> consecutive TRANSIENT
    /// failures (see <see cref="IsTransientFailure"/>) the breaker <b>opens</b> and short-circuits every
    /// subsequent call with an <see cref="LlmErrorCode.BackendUnavailable"/> result <i>without touching the
    /// inner client</i> - a dead primary backend no longer costs <c>timeout x (retries + 1)</c> on every
    /// turn. After <c>openDurationMs</c> the breaker moves to <b>half-open</b> and lets exactly one probe
    /// request through: on success it <b>closes</b>, on failure it opens again for another cool-down.
    /// <para>
    /// Only TRANSIENT failures count (timeout, rate limit, backend unavailable, generic provider error,
    /// routing error) - and it makes no difference whether they arrived as a result, as a terminal chunk,
    /// or as a thrown <see cref="LlmClientException"/>. Caller-fault failures - expired auth, payment
    /// required, invalid request, context overflow, cancellation - NEVER open the breaker: retrying at
    /// another moment will not help. Such exceptions are rethrown as they are, with their type, code and
    /// HTTP status intact, so that outer decorators (retry/fallback) see the same classification they
    /// would without the breaker. A stream that ends without a single chunk is a failure: the backend
    /// answered nothing.
    /// </para>
    /// <para>
    /// Time is supplied as a monotonic millisecond source, which makes the breaker fully deterministic
    /// in tests.
    /// </para>
    /// </summary>
    public sealed class CircuitBreakerLlmClientDecorator : ILlmClient
    {
        private enum State
        {
            Closed,
            Open,
            HalfOpen
        }

        private readonly ILlmClient _inner;
        private readonly int _failureThreshold;
        private readonly long _openDurationMs;
        private readonly Func<long> _nowMs;
        private readonly Action<string> _log;
        private readonly object _gate = new();

        private State _state = State.Closed;
        private int _consecutiveFailures;
        private long _openedAtMs;
        private bool _halfOpenProbeInFlight;
        private long _generation;

        /// <param name="inner">The client being protected.</param>
        /// <param name="failureThreshold">How many consecutive transient failures open the breaker (min 1).</param>
        /// <param name="openDurationMs">How long the breaker stays open before a probe request (min 1).</param>
        /// <param name="nowMs">Monotonic clock in milliseconds (injected for deterministic tests).</param>
        /// <param name="log">Optional one-line diagnostics sink for state transitions.</param>
        public CircuitBreakerLlmClientDecorator(
            ILlmClient inner,
            int failureThreshold,
            long openDurationMs,
            Func<long> nowMs,
            Action<string> log = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _failureThreshold = failureThreshold < 1 ? 1 : failureThreshold;
            _openDurationMs = openDurationMs < 1 ? 1 : openDurationMs;
            _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
            _log = log;
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
            LlmCompletionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnter(out long lease, out string rejectReason))
            {
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = rejectReason,
                    ErrorCode = LlmErrorCode.BackendUnavailable
                };
            }

            bool classified = false;
            try
            {
                LlmCompletionResult result;
                try
                {
                    result = await _inner.CompleteAsync(request, cancellationToken);
                }
                catch (LlmOperationTimeoutException) when (!cancellationToken.IsCancellationRequested)
                {
                    RecordFailure(lease);
                    classified = true;
                    throw;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // WHY: cancellation is the caller's intent, not a backend failure: neither count it nor swallow it.
                    throw;
                }
                catch (LlmClientException ex)
                {
                    // WHY: the typed exception already carries the adapter's classification. Judge by it -
                    // three 401s in a row do not open the breaker - and rethrow it AS IS: wrapping into a
                    // statusless ProviderError made a 402 look retryable to the outer decorators.
                    RecordResult(lease, false, ex.ErrorCode);
                    classified = true;
                    throw;
                }
                catch (Exception)
                {
                    // An untyped throw from the inner client is a transient backend failure; the exception
                    // object itself is rethrown unchanged.
                    RecordFailure(lease);
                    classified = true;
                    throw;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    RecordResult(lease, result?.Ok ?? false, result?.ErrorCode ?? LlmErrorCode.ProviderError);
                    classified = true;
                }
                return result;
            }
            finally
            {
                if (!classified)
                {
                    // WHY: the call ended without a health verdict (cancellation): release the probe slot so
                    // the breaker does not hang waiting for a result that will never come.
                    ReleaseHalfOpenProbe(lease);
                }
            }
        }

        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnter(out long lease, out string rejectReason))
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = rejectReason,
                    ErrorCode = LlmErrorCode.BackendUnavailable
                };
                yield break;
            }

            bool sawTerminalFailure = false;
            LlmErrorCode terminalCode = LlmErrorCode.None;
            bool sawAnyChunk = false;
            bool streamEnded = false;
            bool classified = false;

            IAsyncEnumerator<LlmStreamChunk> e = null;
            try
            {
                // WHY: obtained inside the try so that a synchronous throw from the inner client still goes
                // through the finally that releases the probe slot instead of jamming the breaker.
                e = _inner.CompleteStreamingAsync(request, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

                while (true)
                {
                    LlmStreamChunk chunk;
                    // WHY: C# forbids `yield` inside a catch, so a failure of the inner stream is recorded
                    // here and the terminal chunk is emitted AFTER the try/catch.
                    LlmStreamChunk faultChunk = null;
                    try
                    {
                        if (!await e.MoveNextAsync())
                        {
                            break;
                        }

                        chunk = e.Current;
                    }
                    catch (LlmOperationTimeoutException) when (!cancellationToken.IsCancellationRequested)
                    {
                        RecordFailure(lease);
                        classified = true;
                        faultChunk = new LlmStreamChunk
                        {
                            IsDone = true, Error = "LLM request timed out.", ErrorCode = LlmErrorCode.Timeout
                        };
                        chunk = null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (LlmClientException ex)
                    {
                        // The adapter's classification is preserved in the chunk in full (code, status,
                        // retry-after): a caller-fault failure neither opens the breaker nor becomes a
                        // ProviderError.
                        RecordResult(lease, false, ex.ErrorCode);
                        classified = true;
                        faultChunk = new LlmStreamChunk
                        {
                            IsDone = true,
                            Error = ex.Message,
                            ErrorCode = ex.ErrorCode,
                            HttpStatus = ex.HttpStatus,
                            RetryAfterSeconds = ex.RetryAfterSeconds
                        };
                        chunk = null;
                    }
                    catch (Exception ex)
                    {
                        RecordFailure(lease);
                        classified = true;
                        faultChunk = new LlmStreamChunk
                        {
                            IsDone = true,
                            Error = ex.Message,
                            ErrorCode = LlmErrorCode.ProviderError
                        };
                        chunk = null;
                    }

                    if (faultChunk != null)
                    {
                        yield return faultChunk;
                        yield break;
                    }

                    sawAnyChunk |= chunk != null;
                    if (chunk != null && (!string.IsNullOrEmpty(chunk.Error) || chunk.ErrorCode != LlmErrorCode.None))
                    {
                        sawTerminalFailure = true;
                        terminalCode = chunk.ErrorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : chunk.ErrorCode;
                    }

                    yield return chunk;
                }

                streamEnded = true;
            }
            finally
            {
                try
                {
                    if (e != null)
                    {
                        await e.DisposeAsync();
                    }
                }
                finally
                {
                    // Cleanup may throw, but every admitted lease still receives a verdict or releases
                    // its probe slot. Abandonment without a backend error is not a health failure.
                    if (!classified)
                    {
                        if (cancellationToken.IsCancellationRequested &&
                            (!sawTerminalFailure || terminalCode == LlmErrorCode.Cancelled))
                        {
                            ReleaseHalfOpenProbe(lease);
                        }
                        else if (sawTerminalFailure)
                        {
                            RecordResult(lease, false, terminalCode);
                        }
                        else if (streamEnded && !sawAnyChunk)
                        {
                            RecordFailure(lease);
                        }
                        else if (streamEnded)
                        {
                            RecordSuccess(lease);
                        }
                        else
                        {
                            ReleaseHalfOpenProbe(lease);
                        }
                    }
                }
            }
        }

        /// <summary>Current state name for diagnostics/tests: "Closed", "Open" or "HalfOpen".</summary>
        public string StateName
        {
            get
            {
                lock (_gate)
                {
                    return _state.ToString();
                }
            }
        }

        /// <summary>
        /// Decides whether a call may pass. Moves Open to HalfOpen once the cool-down has elapsed and
        /// admits exactly one probe request. Returns false (with a reason) while the breaker is open.
        /// </summary>
        private bool TryEnter(out long lease, out string rejectReason)
        {
            lock (_gate)
            {
                lease = _generation;
                if (_state == State.Open)
                {
                    if (_nowMs() - _openedAtMs >= _openDurationMs)
                    {
                        _state = State.HalfOpen;
                        lease = ++_generation;
                        _halfOpenProbeInFlight = true;
                        _log?.Invoke("[CircuitBreaker] half-open: admitting one probe request.");
                        rejectReason = null;
                        return true;
                    }

                    rejectReason =
                        "Circuit breaker open: backend is failing; short-circuited to avoid repeated " +
                        "timeouts. It will retry automatically after a cooldown.";
                    return false;
                }

                if (_state == State.HalfOpen)
                {
                    // WHY: in the half-open state exactly ONE probe request may be in flight. Admitting every
                    // concurrent caller dumped the whole backlog onto a backend that is most likely still down.
                    if (_halfOpenProbeInFlight)
                    {
                        rejectReason =
                            "Circuit breaker half-open: a single probe request is already in flight; " +
                            "short-circuited until the probe result is known.";
                        return false;
                    }

                    _halfOpenProbeInFlight = true;
                    lease = ++_generation;
                    _log?.Invoke("[CircuitBreaker] half-open: admitting one probe request.");
                    rejectReason = null;
                    return true;
                }

                rejectReason = null;
                return true;
            }
        }

        /// <summary>
        /// Releases the probe slot when a call ended without a success/failure verdict (the consumer
        /// cancelled the call or abandoned the stream). The breaker stays half-open and the next call
        /// becomes the new probe; an abandoned stream is never counted as a backend failure.
        /// </summary>
        private void ReleaseHalfOpenProbe(long lease)
        {
            lock (_gate)
            {
                if (lease == _generation && _state == State.HalfOpen)
                {
                    _halfOpenProbeInFlight = false;
                }
            }
        }

        private void RecordResult(long lease, bool ok, LlmErrorCode code)
        {
            if (ok)
            {
                RecordSuccess(lease);
                return;
            }

            if (code == LlmErrorCode.None || IsTransientFailure(code))
            {
                RecordFailure(lease);
            }
            else
            {
                // WHY: a caller-fault failure (auth, payment, invalid request, context, empty response) is
                // not a backend-health problem: do not open the breaker; and a probe request that came
                // back with such a result still means the backend is reachable - for the state machine
                // that counts as a soft success.
                RecordSuccess(lease);
            }
        }

        private void RecordSuccess(long lease)
        {
            lock (_gate)
            {
                if (lease != _generation)
                {
                    return;
                }
                _consecutiveFailures = 0;
                _halfOpenProbeInFlight = false;
                if (_state != State.Closed)
                {
                    _state = State.Closed;
                    _generation++;
                    _log?.Invoke("[CircuitBreaker] closed: backend recovered.");
                }
            }
        }

        private void RecordFailure(long lease)
        {
            lock (_gate)
            {
                if (lease != _generation)
                {
                    return;
                }
                _halfOpenProbeInFlight = false;
                if (_state == State.HalfOpen)
                {
                    _state = State.Open;
                    _generation++;
                    _openedAtMs = _nowMs();
                    _log?.Invoke("[CircuitBreaker] re-opened: half-open probe failed.");
                    return;
                }

                _consecutiveFailures++;
                if (_state == State.Closed && _consecutiveFailures >= _failureThreshold)
                {
                    _state = State.Open;
                    _generation++;
                    _openedAtMs = _nowMs();
                    _log?.Invoke(
                        $"[CircuitBreaker] opened after {_consecutiveFailures} consecutive transient failures.");
                }
            }
        }

        /// <summary>
        /// Transient failures worth opening for: the backend is (temporarily) unhealthy, and hammering it
        /// only burns a timeout on every call. Caller-fault codes are excluded: a retry will not help.
        /// </summary>
        private static bool IsTransientFailure(LlmErrorCode code)
        {
            switch (code)
            {
                case LlmErrorCode.Timeout:
                case LlmErrorCode.RateLimited:
                case LlmErrorCode.BackendUnavailable:
                case LlmErrorCode.ProviderError:
                case LlmErrorCode.RoutingError:
                    return true;
                default:
                    return false;
            }
        }
    }
}
#endif
