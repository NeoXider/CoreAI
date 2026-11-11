using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// Per-consumer budgets with a fake clock: calls per second, the in-flight cap and the per-call deadline, each
    /// refusal immediate and typed, with the twin that passes.
    /// </summary>
    [TestFixture]
    public sealed class BudgetedDecisionClientEditModeTests
    {
        private static DecisionRequest Request()
        {
            return new DecisionRequest("s", DecisionQuestion.Choice("action", "pick", "attack", "flee"));
        }

        [Test]
        public async Task Rate_OverTheBucket_IsRefusedAtOnce_AndRefillsWithTime()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient inner = new();
            BudgetedDecisionClient client = new(inner,
                new DecisionBudget { MaxCallsPerSecond = 2, MaxInFlight = 0, DeadlineMilliseconds = 0 }, clock);

            DecisionResult first = await client.DecideAsync(Request());
            DecisionResult second = await client.DecideAsync(Request());
            DecisionResult third = await client.DecideAsync(Request());
            clock.Advance(0.5);
            DecisionResult fourth = await client.DecideAsync(Request());

            Assert.IsTrue(first.Ok);
            Assert.IsTrue(second.Ok);
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, third.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.RateBudgetExceeded, third.ErrorReason);
            Assert.IsTrue(fourth.Ok, "half a second refills one call at 2/s");
            Assert.AreEqual(3, inner.Calls, "a refused call never reaches the backend");
        }

        [Test]
        public async Task FractionalRate_AllowsOneCallPerInterval()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient inner = new();
            BudgetedDecisionClient client = new(inner,
                new DecisionBudget { MaxCallsPerSecond = 0.5, MaxInFlight = 0, DeadlineMilliseconds = 0 }, clock);

            Assert.IsTrue((await client.DecideAsync(Request())).Ok);
            clock.Advance(1.9);
            Assert.IsFalse((await client.DecideAsync(Request())).Ok);
            clock.Advance(0.1);
            Assert.IsTrue((await client.DecideAsync(Request())).Ok);
        }

        [Test]
        public async Task InFlight_OverTheCap_IsRefused_UntilTheCallFinishes()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient inner = new() { HoldAnswers = true };
            BudgetedDecisionClient client = new(inner,
                new DecisionBudget { MaxCallsPerSecond = 0, MaxInFlight = 1, DeadlineMilliseconds = 0 }, clock);

            Task<DecisionResult> first = client.DecideAsync(Request());
            DecisionResult second = await client.DecideAsync(Request());
            inner.CompleteNext(DecisionFixtures.Answer(Request(), "flee", 0.9));
            DecisionResult firstResult = await first;
            inner.HoldAnswers = false;
            DecisionResult third = await client.DecideAsync(Request());

            Assert.AreEqual(DecisionErrorReasons.InFlightBudgetExceeded, second.ErrorReason);
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, second.ErrorCode);
            Assert.IsTrue(firstResult.Ok);
            Assert.IsTrue(third.Ok);
            Assert.AreEqual(0, client.InFlight);
        }

        [Test]
        public async Task Deadline_Missed_IsATimeout_AndCancelsTheInnerCall()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient inner = new() { HoldAnswers = true };
            BudgetedDecisionClient client = new(inner,
                new DecisionBudget { MaxCallsPerSecond = 0, MaxInFlight = 0, DeadlineMilliseconds = 900 }, clock);

            Task<DecisionResult> call = client.DecideAsync(Request());
            clock.Advance(0.5);
            Assert.IsFalse(call.IsCompleted, "before the deadline the call is still waiting");
            clock.Advance(0.4);
            DecisionResult result = await call;

            Assert.AreEqual(LlmErrorCode.Timeout, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.DeadlineExceeded, result.ErrorReason);
            Assert.IsTrue(inner.Tokens[0].IsCancellationRequested, "the abandoned call is told to stop");
        }

        [Test]
        public async Task Deadline_Met_PassesTheResultThrough_AndStopsTheDeadline()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient inner = new() { HoldAnswers = true };
            BudgetedDecisionClient client = new(inner,
                new DecisionBudget { MaxCallsPerSecond = 0, MaxInFlight = 0, DeadlineMilliseconds = 900 }, clock);

            Task<DecisionResult> call = client.DecideAsync(Request());
            clock.Advance(0.3);
            inner.CompleteNext(DecisionFixtures.Answer(Request(), "attack", 0.8));
            DecisionResult result = await call;

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("attack", result.Find("action").ChosenOptionId);
            Assert.AreEqual(0, clock.PendingDelays, "the deadline delay is cancelled once the call answers");
        }

        [Test]
        public async Task AbandonedCall_KeepsItsSlot_UntilItReallyFinishes()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient inner = new() { HoldAnswers = true, HonourCancellation = false };
            BudgetedDecisionClient client = new(inner,
                new DecisionBudget { MaxCallsPerSecond = 0, MaxInFlight = 1, DeadlineMilliseconds = 100 }, clock);

            Task<DecisionResult> call = client.DecideAsync(Request());
            clock.Advance(0.1);
            DecisionResult timedOut = await call;
            DecisionResult whileStuck = await client.DecideAsync(Request());
            inner.CompleteNext(DecisionFixtures.Answer(Request(), "flee", 0.9));

            Assert.AreEqual(DecisionErrorReasons.DeadlineExceeded, timedOut.ErrorReason);
            Assert.AreEqual(DecisionErrorReasons.InFlightBudgetExceeded, whileStuck.ErrorReason);
            Assert.AreEqual(0, client.InFlight, "the slot frees when the backend finally answers");
        }

        [Test]
        public async Task CallerCancellation_BeforeTheDeadline_Throws()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient inner = new() { HoldAnswers = true, HonourCancellation = false };
            BudgetedDecisionClient client = new(inner, new DecisionBudget { DeadlineMilliseconds = 900 }, clock);
            using CancellationTokenSource cancellation = new();

            Task<DecisionResult> call = client.DecideAsync(Request(), cancellation.Token);
            cancellation.Cancel();

            await DecisionAsserts.AwaitThrows<OperationCanceledException>(() => call);
            Assert.IsTrue(inner.Tokens[0].IsCancellationRequested);
        }

        [Test]
        public async Task Budget_IsReadOnEveryCall()
        {
            ManualDecisionClock clock = new();
            DecisionBudget budget = new() { MaxCallsPerSecond = 0, MaxInFlight = 0, DeadlineMilliseconds = 0 };
            ScriptedDecisionClient inner = new() { HoldAnswers = true };
            BudgetedDecisionClient client = new(inner, () => budget, clock);

            Task<DecisionResult> first = client.DecideAsync(Request());
            budget = new DecisionBudget { MaxCallsPerSecond = 0, MaxInFlight = 1, DeadlineMilliseconds = 0 };
            DecisionResult second = await client.DecideAsync(Request());
            inner.CompleteNext(DecisionFixtures.Answer(Request(), "flee", 0.9));
            await first;

            Assert.AreEqual(DecisionErrorReasons.InFlightBudgetExceeded, second.ErrorReason);
        }

        [Test]
        public async Task NullBudget_IsUnlimited()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient inner = new();
            BudgetedDecisionClient client = new(inner, () => null, clock);

            for (int i = 0; i < 50; i++)
            {
                Assert.IsTrue((await client.DecideAsync(Request())).Ok);
            }

            Assert.AreEqual(0, clock.PendingDelays);
        }
    }
}
