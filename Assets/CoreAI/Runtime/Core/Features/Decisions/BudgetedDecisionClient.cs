using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>
    /// Enforces one consumer's <see cref="DecisionBudget"/> around an inner client: a calls-per-second token
    /// bucket, an in-flight cap and a per-call deadline. Over budget the call is refused immediately with
    /// <see cref="LlmErrorCode.ClientLimitExceeded"/>; a missed deadline returns <see cref="LlmErrorCode.Timeout"/>
    /// with <see cref="DecisionErrorReasons.DeadlineExceeded"/> and cancels the inner call.
    /// <para>
    /// The deadline is scheduled through <see cref="IDecisionClock.DelayAsync"/>, never a timer-backed
    /// cancellation, so it fires in a WebGL player. An inner call keeps its in-flight slot until it really
    /// finishes, even after its deadline: a backend that ignores cancellation must not be allowed to pile up
    /// requests behind a count that says it is idle.
    /// </para>
    /// </summary>
    public sealed class BudgetedDecisionClient : IDecisionClient
    {
        private readonly IDecisionClient _inner;
        private readonly Func<DecisionBudget> _budget;
        private readonly IDecisionClock _clock;
        private readonly object _gate = new();
        private double _tokens = double.NaN;
        private double _lastRefill;
        private int _inFlight;

        /// <summary>Creates a decorator with a budget read on every call (so a route change applies at once).</summary>
        public BudgetedDecisionClient(IDecisionClient inner, Func<DecisionBudget> budget, IDecisionClock clock)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _budget = budget ?? (() => null);
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>Creates a decorator with a fixed budget.</summary>
        public BudgetedDecisionClient(IDecisionClient inner, DecisionBudget budget, IDecisionClock clock)
            : this(inner, () => budget, clock)
        {
        }

        /// <summary>Calls currently holding an in-flight slot.</summary>
        public int InFlight
        {
            get
            {
                lock (_gate)
                {
                    return _inFlight;
                }
            }
        }

        /// <inheritdoc />
        public async Task<DecisionResult> DecideAsync(DecisionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DecisionBudget budget = _budget() ?? DecisionBudget.Unlimited();
            DecisionResult refusal = TryAdmit(budget);
            if (refusal != null)
            {
                return refusal;
            }

            CancellationTokenSource callCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<DecisionResult> call;
            try
            {
                call = _inner.DecideAsync(request, callCancellation.Token);
            }
            catch (Exception)
            {
                Release(callCancellation);
                throw;
            }

            _ = ReleaseWhenFinishedAsync(call, callCancellation);
            if (budget.DeadlineMilliseconds <= 0)
            {
                return await call;
            }

            using CancellationTokenSource deadlineCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task deadline = _clock.DelayAsync(budget.DeadlineMilliseconds, deadlineCancellation.Token);
            Task first = await Task.WhenAny(call, deadline);
            if (first == call)
            {
                deadlineCancellation.Cancel();
                _ = ObserveQuietlyAsync(deadline);
                return await call;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                TryCancel(callCancellation);
                throw new OperationCanceledException(cancellationToken);
            }

            if (deadline.Status != TaskStatus.RanToCompletion)
            {
                // WHY: a faulted host delay is not an expired deadline; the call keeps running unbounded rather
                // than being reported as a timeout that never happened.
                _ = ObserveQuietlyAsync(deadline);
                return await call;
            }

            TryCancel(callCancellation);
            return DecisionResult.Failure(LlmErrorCode.Timeout, DecisionErrorReasons.DeadlineExceeded,
                $"The decision call missed its {budget.DeadlineMilliseconds} ms deadline.", "budget");
        }

        private DecisionResult TryAdmit(DecisionBudget budget)
        {
            double now = _clock.NowSeconds;
            lock (_gate)
            {
                if (budget.MaxInFlight > 0 && _inFlight >= budget.MaxInFlight)
                {
                    return DecisionResult.Failure(LlmErrorCode.ClientLimitExceeded,
                        DecisionErrorReasons.InFlightBudgetExceeded,
                        $"The consumer already has {_inFlight} decision call(s) in flight (cap {budget.MaxInFlight}).",
                        "budget");
                }

                double rate = budget.MaxCallsPerSecond;
                if (rate > 0d && !double.IsInfinity(rate))
                {
                    double capacity = Math.Max(1d, rate);
                    if (double.IsNaN(_tokens))
                    {
                        _tokens = capacity;
                    }
                    else
                    {
                        _tokens = Math.Min(capacity, _tokens + Math.Max(0d, now - _lastRefill) * rate);
                    }

                    _lastRefill = now;
                    if (_tokens < 1d)
                    {
                        return DecisionResult.Failure(LlmErrorCode.ClientLimitExceeded,
                            DecisionErrorReasons.RateBudgetExceeded,
                            $"The consumer's decision budget of {rate} call(s) per second is spent.", "budget");
                    }

                    _tokens -= 1d;
                }

                _inFlight++;
                return null;
            }
        }

        private async Task ReleaseWhenFinishedAsync(Task<DecisionResult> call, CancellationTokenSource cancellation)
        {
            try
            {
                await call;
            }
            catch (Exception)
            {
                // WHY: the caller observes the outcome through its own await; this continuation only frees the slot.
            }
            finally
            {
                Release(cancellation);
            }
        }

        private void Release(CancellationTokenSource cancellation)
        {
            lock (_gate)
            {
                _inFlight = Math.Max(0, _inFlight - 1);
            }

            cancellation.Dispose();
        }

        private static void TryCancel(CancellationTokenSource cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // WHY: the call finished and released its source between the race and this cancel.
            }
        }

        private static async Task ObserveQuietlyAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                // WHY: a cancelled or faulted deadline delay has no one left to report to.
            }
        }
    }
}
