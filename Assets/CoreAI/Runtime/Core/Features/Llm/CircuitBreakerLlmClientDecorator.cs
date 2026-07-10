#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Circuit breaker for an <see cref="ILlmClient"/>. After a backend returns
    /// <see cref="TransientFailure"/> results <c>failureThreshold</c> times in a row, the breaker trips
    /// <b>open</b> and short-circuits subsequent calls with a <see cref="LlmErrorCode.BackendUnavailable"/>
    /// result <i>without invoking the inner client</i> — so a dead primary no longer costs
    /// <c>timeout × (retries + 1)</c> on every turn. After <c>openDurationMs</c> the breaker moves to
    /// <b>half-open</b> and lets a single probe through: if it succeeds the breaker <b>closes</b>; if it
    /// fails it re-opens for another cooldown.
    /// <para>
    /// Only TRANSIENT failures count toward tripping (timeouts, rate limits, backend-unavailable, generic
    /// provider errors). Caller-caused failures — auth expiry, invalid request, context-length exceeded,
    /// cancellation — never trip the breaker, because retrying a different moment would not help.
    /// </para>
    /// <para>
    /// Time is injected as a monotonic millisecond source so the breaker is fully deterministic under test.
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

        /// <param name="inner">The client to protect.</param>
        /// <param name="failureThreshold">Consecutive transient failures that trip the breaker (min 1).</param>
        /// <param name="openDurationMs">How long the breaker stays open before a half-open probe (min 1).</param>
        /// <param name="nowMs">Monotonic millisecond clock (injected for deterministic tests).</param>
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

        public bool SupportsNativeToolCallingForRole(string agentRoleId) =>
            _inner.SupportsNativeToolCallingForRole(agentRoleId);

        public void SetTools(IReadOnlyList<ILlmTool> tools) => _inner.SetTools(tools);

        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request, CancellationToken cancellationToken = default)
        {
            if (!TryEnter(out string rejectReason))
            {
                return new LlmCompletionResult
                {
                    Ok = false,
                    Error = rejectReason,
                    ErrorCode = LlmErrorCode.BackendUnavailable
                };
            }

            LlmCompletionResult result;
            try
            {
                result = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is caller intent, not a backend fault — do not count it, do not swallow it.
                throw;
            }
            catch (Exception ex)
            {
                // An unexpected throw from the inner client is treated as a transient backend fault.
                RecordFailure();
                throw new LlmClientException(ex.Message, LlmErrorCode.ProviderError);
            }

            RecordResult(result?.Ok ?? false, result?.ErrorCode ?? LlmErrorCode.ProviderError);
            return result;
        }

        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!TryEnter(out string rejectReason))
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

            IAsyncEnumerator<LlmStreamChunk> e =
                _inner.CompleteStreamingAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    LlmStreamChunk chunk;
                    // C# forbids `yield` inside a catch, so capture any inner-stream fault here and emit the
                    // terminal error chunk AFTER the try/catch instead.
                    string moveError = null;
                    try
                    {
                        if (!await e.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        chunk = e.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RecordFailure();
                        moveError = ex.Message;
                        chunk = default;
                    }

                    if (moveError != null)
                    {
                        yield return new LlmStreamChunk
                        {
                            IsDone = true,
                            Error = moveError,
                            ErrorCode = LlmErrorCode.ProviderError
                        };
                        yield break;
                    }

                    sawAnyChunk = true;
                    if (!string.IsNullOrEmpty(chunk.Error) && chunk.ErrorCode != LlmErrorCode.None)
                    {
                        sawTerminalFailure = true;
                        terminalCode = chunk.ErrorCode;
                    }

                    yield return chunk;
                }
            }
            finally
            {
                await e.DisposeAsync().ConfigureAwait(false);
            }

            // Classify the whole stream: a stream that produced an error chunk (or nothing at all) is a
            // failure; a stream that ended cleanly is a success that closes/keeps-closed the breaker.
            bool ok = sawAnyChunk && !sawTerminalFailure;
            RecordResult(ok, ok ? LlmErrorCode.None : terminalCode);
        }

        /// <summary>Current state name for diagnostics/tests: "Closed", "Open", or "HalfOpen".</summary>
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
        /// Decides whether a call may proceed. Transitions Open→HalfOpen once the cooldown elapses and
        /// admits exactly one probe. Returns false (with a reason) while the breaker is open.
        /// </summary>
        private bool TryEnter(out string rejectReason)
        {
            lock (_gate)
            {
                if (_state == State.Open)
                {
                    if (_nowMs() - _openedAtMs >= _openDurationMs)
                    {
                        _state = State.HalfOpen;
                        _log?.Invoke("[CircuitBreaker] half-open: admitting one probe request.");
                        rejectReason = null;
                        return true;
                    }

                    rejectReason =
                        "Circuit breaker open: backend is failing; short-circuited to avoid repeated " +
                        "timeouts. It will retry automatically after a cooldown.";
                    return false;
                }

                // Closed or HalfOpen: allow through (HalfOpen admits the single probe already in flight).
                rejectReason = null;
                return true;
            }
        }

        private void RecordResult(bool ok, LlmErrorCode code)
        {
            if (ok)
            {
                RecordSuccess();
                return;
            }

            if (IsTransientFailure(code))
            {
                RecordFailure();
            }
            else
            {
                // A caller-caused failure (auth, invalid request, context length, empty) is not the backend's
                // health problem — do not trip the breaker, but a half-open probe that returned such a result
                // still means the backend is reachable, so treat it as a soft success for state purposes.
                RecordSuccess();
            }
        }

        private void RecordSuccess()
        {
            lock (_gate)
            {
                _consecutiveFailures = 0;
                if (_state != State.Closed)
                {
                    _state = State.Closed;
                    _log?.Invoke("[CircuitBreaker] closed: backend recovered.");
                }
            }
        }

        private void RecordFailure()
        {
            lock (_gate)
            {
                if (_state == State.HalfOpen)
                {
                    // The probe failed — re-open for another cooldown.
                    _state = State.Open;
                    _openedAtMs = _nowMs();
                    _log?.Invoke("[CircuitBreaker] re-opened: half-open probe failed.");
                    return;
                }

                _consecutiveFailures++;
                if (_state == State.Closed && _consecutiveFailures >= _failureThreshold)
                {
                    _state = State.Open;
                    _openedAtMs = _nowMs();
                    _log?.Invoke(
                        $"[CircuitBreaker] opened after {_consecutiveFailures} consecutive transient failures.");
                }
            }
        }

        /// <summary>
        /// Transient failures worth breaking on: the backend is (temporarily) unhealthy and hammering it just
        /// wastes the per-call timeout. Caller-caused codes are excluded — retrying would not help.
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
