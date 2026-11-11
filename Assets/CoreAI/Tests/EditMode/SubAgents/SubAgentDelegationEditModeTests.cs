using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// The delegate tool body with scripted sub-agent runners: context isolation, no nested delegation,
    /// parallelism and budget caps, the result as the tool result, deadlines and the cancellation fan-out.
    /// </summary>
    [TestFixture]
    public sealed class SubAgentDelegationEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY: the scripted runs complete inline; without a context their continuations run on the completing
        /// call, so an assertion right after a completion sees the next run already started.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [Test]
        public async Task SubAgentRequest_CarriesOnlyTheTaskAndThePassedContext()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            session.BeginPlan("make me a survival game PARENT-GOAL-MARKER", SubAgentTestSupport.Parent());

            SubAgentDelegationResult result =
                await SubAgentTestSupport.Delegate(session, "Build four walls", context: "Origin is 0,0,0");

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual(1, runner.Requests.Count);
            AiTaskRequest captured = runner.Requests[0];
            Assert.AreEqual("Build four walls\n\n" + SubAgentRequestFactory.ContextHeading + "\nOrigin is 0,0,0",
                captured.Hint);
            string everythingSent = captured.Hint + captured.SystemPrompt + captured.RequestSystemInstructions;
            StringAssert.DoesNotContain("PARENT-GOAL-MARKER", everythingSent,
                "The plan goal is the lead's conversation; a sub-agent only sees what the lead passes.");
            Assert.IsNull(captured.Attachments);
            Assert.AreEqual("", captured.SystemPrompt);
            Assert.AreEqual("Report what you built.", captured.RequestSystemInstructions);
            Assert.AreEqual("Builder", captured.RoleId);
            Assert.AreEqual("fast", captured.RoutingProfileId);
            Assert.AreEqual(SubAgentRequestFactory.SourceTag, captured.SourceTag);
            Assert.AreEqual(512, captured.MaxOutputTokens);
            Assert.AreEqual(6, captured.MaxToolCallRoundtrips);
            CollectionAssert.AreEqual(new[] { "world_command" }, captured.AllowedToolNames);
            Assert.IsTrue(captured.ActorContext.HasValue);
            Assert.AreEqual(SubAgentTestSupport.ActorId, captured.ActorContext.Value.ActorId);
            Assert.AreEqual(3, captured.Priority);
            StringAssert.StartsWith(SubAgentTestSupport.LeadTrace + ".sub.", captured.TraceId);
            Assert.AreNotEqual(SubAgentTestSupport.LeadScope, captured.CancellationScope);
        }

        [Test]
        public async Task SecondSubAgent_DoesNotSeeTheFirstResult_UnlessTheLeadPassesIt()
        {
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (task, ct) => Task.FromResult(SubAgentTestSupport.Answer("FIRST-RESULT-MARKER"))
            };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            await SubAgentTestSupport.Delegate(session, "Build walls");
            await SubAgentTestSupport.Delegate(session, "Script a door", "scripter");
            await SubAgentTestSupport.Delegate(session, "Script a gate", "scripter",
                context: "The walls report: FIRST-RESULT-MARKER");

            StringAssert.DoesNotContain("FIRST-RESULT-MARKER", runner.Requests[1].Hint);
            StringAssert.Contains("FIRST-RESULT-MARKER", runner.Requests[2].Hint,
                "Context the lead chooses to pass is the one way a fact crosses between sub-agents.");
        }

        [Test]
        public async Task EveryRun_GetsItsOwnTraceAndCancellationScope()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            await SubAgentTestSupport.Delegate(session, "one");
            await SubAgentTestSupport.Delegate(session, "two");
            await SubAgentTestSupport.Delegate(session, "one again", taskId: "t1");

            HashSet<string> traces = new();
            HashSet<string> scopes = new();
            foreach (AiTaskRequest request in runner.Requests)
            {
                Assert.IsTrue(traces.Add(request.TraceId), "A retried task must not reuse a trace id.");
                Assert.IsTrue(scopes.Add(request.CancellationScope),
                    "A shared scope is latest-wins in the queue and would cancel a sibling.");
                Assert.AreNotEqual(SubAgentTestSupport.LeadScope, request.CancellationScope);
            }
        }

        [Test]
        public async Task SubAgentCallingDelegate_IsRefused_AndNoSecondRunStarts()
        {
            OrchestratorSession session = null;
            SubAgentDelegationResult nested = null;
            ScriptedSubAgentRunner runner = new();
            runner.OnRun = async (task, ct) =>
            {
                nested = await SubAgentTestSupport.Delegate(session, "delegate further", cancellationToken: ct);
                return SubAgentTestSupport.Answer("done myself");
            };
            session = SubAgentTestSupport.Session(runner);
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult outer = await SubAgentTestSupport.Delegate(session, "Build walls");

            Assert.IsTrue(outer.Ok, outer.Message);
            Assert.AreEqual(1, runner.Requests.Count, "The nested call must never reach the runner.");
            Assert.IsNotNull(nested);
            Assert.AreEqual(SubAgentErrorCode.NestedDelegation, nested.ErrorCode);
            StringAssert.Contains("\"error\":\"nested_delegation_refused\"", nested.ToToolResultJson());
        }

        [Test]
        public async Task SubAgentCallingTheDelegateFunction_IsRefusedThroughTheToolBinding()
        {
            OrchestratorSession session = null;
            string nestedJson = null;
            ScriptedSubAgentRunner runner = new();
            runner.OnRun = async (task, ct) =>
            {
                AIFunction function = session.DelegateTool.CreateAIFunction();
                object raw = await function.InvokeAsync(
                    new AIFunctionArguments { ["subagent"] = "builder", ["task"] = "delegate further" }, ct);
                nestedJson = SubAgentTestSupport.AsText(raw);
                return SubAgentTestSupport.Answer("done myself");
            };
            session = SubAgentTestSupport.Session(runner);
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult outer = await SubAgentTestSupport.Delegate(session, "Build walls");

            Assert.IsTrue(outer.Ok, outer.Message);
            Assert.AreEqual(1, runner.Requests.Count);
            JObject parsed = JObject.Parse(nestedJson);
            Assert.AreEqual(false, (bool)parsed["ok"]);
            Assert.AreEqual("nested_delegation_refused", (string)parsed["error"]);
        }

        [Test]
        public async Task Lead_SequentialDelegations_AreNotMistakenForNesting()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult first = await SubAgentTestSupport.Delegate(session, "one");
            SubAgentDelegationResult second = await SubAgentTestSupport.Delegate(session, "two");

            Assert.IsTrue(first.Ok, first.Message);
            Assert.IsTrue(second.Ok, second.Message);
            Assert.IsFalse(session.Delegator.IsInsideSubAgentRun);
            Assert.AreEqual(2, runner.Requests.Count);
        }

        [Test]
        public async Task DefaultParallelism_RunsOneSubAgentAtATime_InCallOrder()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());

            Task<SubAgentDelegationResult> a = SubAgentTestSupport.Delegate(session, "A");
            Task<SubAgentDelegationResult> b = SubAgentTestSupport.Delegate(session, "B");
            Task<SubAgentDelegationResult> c = SubAgentTestSupport.Delegate(session, "C");

            Assert.AreEqual(1, runner.Requests.Count, "Parallelism 1 is the default for one local endpoint.");
            OrchestrationPlanSnapshot snapshot = plan.Snapshot();
            Assert.AreEqual(PlanTaskState.Running, snapshot.Tasks[0].State);
            Assert.AreEqual(PlanTaskState.Waiting, snapshot.Tasks[1].State);
            Assert.AreEqual(PlanTaskState.Waiting, snapshot.Tasks[2].State);

            runner.Held[0].TrySetResult(SubAgentTestSupport.Answer("a"));
            await a;
            Assert.IsTrue(await SubAgentTestSupport.EventuallyAsync(() => runner.Requests.Count == 2));
            Assert.AreEqual("B", runner.Requests[1].Hint);
            runner.Held[1].TrySetResult(SubAgentTestSupport.Answer("b"));
            await b;
            Assert.IsTrue(await SubAgentTestSupport.EventuallyAsync(() => runner.Requests.Count == 3));
            Assert.AreEqual("C", runner.Requests[2].Hint);
            runner.Held[2].TrySetResult(SubAgentTestSupport.Answer("c"));
            await c;

            Assert.AreEqual(1, runner.MaxActive);
            Assert.AreEqual(3, plan.Snapshot().CompletedTasks);
        }

        [Test]
        public async Task ParallelismTwo_RunsTwoSubAgentsAtOnce_AndNoMore()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner,
                new OrchestratorModeOptions { MaxParallelSubAgents = 2 });
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            Task<SubAgentDelegationResult> a = SubAgentTestSupport.Delegate(session, "A");
            Task<SubAgentDelegationResult> b = SubAgentTestSupport.Delegate(session, "B");
            Task<SubAgentDelegationResult> c = SubAgentTestSupport.Delegate(session, "C");

            Assert.AreEqual(2, runner.Requests.Count);
            runner.Held[1].TrySetResult(SubAgentTestSupport.Answer("b"));
            await b;
            Assert.IsTrue(await SubAgentTestSupport.EventuallyAsync(() => runner.Requests.Count == 3));
            runner.Held[0].TrySetResult(SubAgentTestSupport.Answer("a"));
            runner.Held[2].TrySetResult(SubAgentTestSupport.Answer("c"));
            await Task.WhenAll(a, c);

            Assert.AreEqual(2, runner.MaxActive);
        }

        [Test]
        public async Task CallBudget_RefusesTheCallPastIt_WithATypedReason_AndThePlanReportsIt()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner,
                new OrchestratorModeOptions { MaxSubAgentCallsPerPlan = 2 });
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult first = await SubAgentTestSupport.Delegate(session, "one");
            SubAgentDelegationResult second = await SubAgentTestSupport.Delegate(session, "two");
            SubAgentDelegationResult third = await SubAgentTestSupport.Delegate(session, "three");
            SubAgentDelegationResult fourth = await SubAgentTestSupport.Delegate(session, "four");

            Assert.IsTrue(first.Ok && second.Ok);
            Assert.AreEqual(SubAgentErrorCode.CallBudgetExhausted, third.ErrorCode);
            Assert.AreEqual(SubAgentErrorCode.CallBudgetExhausted, fourth.ErrorCode);
            Assert.AreEqual(2, runner.Requests.Count, "A refused delegation never reaches the runner.");
            JObject json = JObject.Parse(third.ToToolResultJson());
            Assert.AreEqual(false, (bool)json["ok"]);
            Assert.AreEqual("call_budget_exhausted", (string)json["error"]);
            OrchestrationPlanSnapshot snapshot = plan.Snapshot();
            Assert.AreEqual(OrchestrationPlanState.BudgetExhausted, snapshot.State);
            Assert.AreEqual(SubAgentErrorCode.CallBudgetExhausted, snapshot.ExhaustedBudget);
            StringAssert.StartsWith("budget exhausted (calls)", OrchestrationStatusText.FormatLine(session.Current));
        }

        [Test]
        public async Task CallsWithinTheBudget_AreNotRefused()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner,
                new OrchestratorModeOptions { MaxSubAgentCallsPerPlan = 2 });
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult first = await SubAgentTestSupport.Delegate(session, "one");
            SubAgentDelegationResult second = await SubAgentTestSupport.Delegate(session, "two");

            Assert.IsTrue(first.Ok && second.Ok);
            Assert.AreEqual(OrchestrationPlanState.Running, plan.State);
            Assert.AreEqual(2, plan.Snapshot().Budget.CallsStarted);
        }

        [Test]
        public async Task TokenBudget_TheRunThatCrossesItFinishes_TheNextIsRefused()
        {
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (task, ct) => Task.FromResult(SubAgentTestSupport.Answer("big", 150))
            };
            OrchestratorSession session = SubAgentTestSupport.Session(runner,
                new OrchestratorModeOptions { MaxTokensPerPlan = 100 });
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult first = await SubAgentTestSupport.Delegate(session, "one");
            SubAgentDelegationResult second = await SubAgentTestSupport.Delegate(session, "two");

            Assert.IsTrue(first.Ok, first.Message);
            Assert.AreEqual(SubAgentErrorCode.TokenBudgetExhausted, second.ErrorCode);
            Assert.AreEqual(1, runner.Requests.Count);
            Assert.AreEqual(150, plan.Snapshot().Budget.TokensUsed);
            StringAssert.Contains("token_budget_exhausted", second.ToToolResultJson());
        }

        [Test]
        public async Task TokenBudget_NotReached_RefusesNothing()
        {
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (task, ct) => Task.FromResult(SubAgentTestSupport.Answer("small", 40))
            };
            OrchestratorSession session = SubAgentTestSupport.Session(runner,
                new OrchestratorModeOptions { MaxTokensPerPlan = 100 });
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult first = await SubAgentTestSupport.Delegate(session, "one");
            SubAgentDelegationResult second = await SubAgentTestSupport.Delegate(session, "two");

            Assert.IsTrue(first.Ok && second.Ok);
            Assert.AreEqual(2, runner.Requests.Count);
        }

        [Test]
        public async Task CancelledWhileWaiting_RefundsItsCall()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner,
                new OrchestratorModeOptions { MaxSubAgentCallsPerPlan = 2 });
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());
            using CancellationTokenSource leadCall = new();

            Task<SubAgentDelegationResult> running = SubAgentTestSupport.Delegate(session, "A");
            Task<SubAgentDelegationResult> waiting =
                SubAgentTestSupport.Delegate(session, "B", cancellationToken: leadCall.Token);
            leadCall.Cancel();
            bool threwCancellation = false;
            try
            {
                await waiting;
            }
            catch (OperationCanceledException)
            {
                threwCancellation = true;
            }

            Assert.IsTrue(threwCancellation, "A cancelled lead invocation surfaces as cancellation, not a result.");
            Assert.AreEqual(1, plan.Snapshot().Budget.CallsStarted, "The call that never ran is refunded.");
            runner.Held[0].TrySetResult(SubAgentTestSupport.Answer("a"));
            await running;
            SubAgentDelegationResult third =
                await RunHeldAsync(runner, () => SubAgentTestSupport.Delegate(session, "C"));
            Assert.IsTrue(third.Ok, third.Message);
            Assert.AreEqual(2, runner.Requests.Count);
        }

        [Test]
        public async Task Result_IsReturnedAsTheToolResult()
        {
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (task, ct) => Task.FromResult(SubAgentTestSupport.Answer("Built 4 walls at 0,0,0", 20))
            };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            session.BeginPlan("goal", SubAgentTestSupport.Parent());
            AIFunction function = session.DelegateTool.CreateAIFunction();

            object raw = await function.InvokeAsync(new AIFunctionArguments
            {
                ["subagent"] = "builder",
                ["task"] = "Build walls",
                ["context"] = "origin 0,0,0"
            });

            string json = SubAgentTestSupport.AsText(raw);
            JObject parsed = JObject.Parse(json);
            Assert.AreEqual(true, (bool)parsed["ok"]);
            Assert.AreEqual("Built 4 walls at 0,0,0", (string)parsed["result"]);
            Assert.AreEqual("builder", (string)parsed["subagent"]);
            Assert.AreEqual("t1", (string)parsed["task_id"]);
            Assert.AreEqual(false, (bool)parsed["truncated"]);
            Assert.AreEqual("Build walls\n\n" + SubAgentRequestFactory.ContextHeading + "\norigin 0,0,0",
                runner.Requests[0].Hint);
#if COREAI_LLM
            Assert.IsTrue(CoreAI.Infrastructure.Llm.ToolExecutionPolicy.IsToolResultSuccess(json));
#endif
        }

        [Test]
        public async Task FailedSubAgent_IsAFailedToolResult()
        {
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (task, ct) => Task.FromResult(new LlmCompletionResult
                {
                    Ok = false,
                    Error = "backend down",
                    ErrorCode = LlmErrorCode.BackendUnavailable
                })
            };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "Build walls");

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(SubAgentErrorCode.SubAgentFailed, result.ErrorCode);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, result.ProviderErrorCode);
            string json = result.ToToolResultJson();
            StringAssert.StartsWith("{\"ok\":false", json, "ok comes first so a size cut never hides it.");
            StringAssert.Contains("backend down", json);
            Assert.AreEqual(PlanTaskState.Failed, plan.Snapshot().Tasks[0].State);
            Assert.AreEqual(OrchestrationPlanState.Running, plan.State, "One failed task does not end the plan.");
#if COREAI_LLM
            Assert.IsFalse(CoreAI.Infrastructure.Llm.ToolExecutionPolicy.IsToolResultSuccess(json));
#endif
        }

        [Test]
        public async Task ThrowingSubAgent_IsAFailedToolResult()
        {
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (task, ct) => Task.FromException<LlmCompletionResult>(new InvalidOperationException("kaput"))
            };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "Build walls");

            Assert.AreEqual(SubAgentErrorCode.SubAgentFailed, result.ErrorCode);
            StringAssert.Contains("kaput", result.Message);
        }

        [Test]
        public async Task LongResult_IsClippedToTheDefinitionCap()
        {
            string answer = new string('x', 500);
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (task, ct) => Task.FromResult(SubAgentTestSupport.Answer(answer))
            };
            OrchestratorSession session = SubAgentTestSupport.Session(runner, null, null,
                SubAgentTestSupport.Builder(maxResultChars: 60));
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "Build walls");

            Assert.IsTrue(result.Ok);
            Assert.IsTrue(result.Truncated);
            Assert.LessOrEqual(result.Content.Length, 60);
            StringAssert.Contains("clipped", result.Content);
            Assert.AreEqual(true, (bool)JObject.Parse(result.ToToolResultJson())["truncated"]);
        }

        [Test]
        public async Task ShortResult_IsNotClipped()
        {
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (task, ct) => Task.FromResult(SubAgentTestSupport.Answer("short"))
            };
            OrchestratorSession session = SubAgentTestSupport.Session(runner, null, null,
                SubAgentTestSupport.Builder(maxResultChars: 60));
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "Build walls");

            Assert.AreEqual("short", result.Content);
            Assert.IsFalse(result.Truncated);
        }

        [Test]
        public async Task LongContext_IsClippedToTheOptionsCap()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner,
                new OrchestratorModeOptions { MaxContextChars = 40 });
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            await SubAgentTestSupport.Delegate(session, "Build walls", context: new string('c', 400));

            string hint = runner.Requests[0].Hint;
            string context = hint.Substring(hint.IndexOf(SubAgentRequestFactory.ContextHeading,
                StringComparison.Ordinal) + SubAgentRequestFactory.ContextHeading.Length + 1);
            Assert.LessOrEqual(context.Length, 40);
            StringAssert.Contains("clipped", context);
        }

        [Test]
        public async Task Stop_CancelsEveryInFlightRequest_AndTheWaitingOne_WithinOneSecond()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner,
                new OrchestratorModeOptions { MaxParallelSubAgents = 2 });
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());
            Task<SubAgentDelegationResult> a = SubAgentTestSupport.Delegate(session, "A");
            Task<SubAgentDelegationResult> b = SubAgentTestSupport.Delegate(session, "B", "scripter");
            Task<SubAgentDelegationResult> c = SubAgentTestSupport.Delegate(session, "C");
            Assert.AreEqual(2, runner.Requests.Count);

            bool stopped = session.Stop();

            Assert.IsTrue(stopped);
            foreach (CancellationToken token in runner.Tokens)
            {
                Assert.IsTrue(token.IsCancellationRequested, "Stop reaches every in-flight request at once.");
            }

            Task all = Task.WhenAll(a, b, c);
            Task first = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.AreSame(all, first, "Every delegation ends within one second of Stop.");
            foreach (Task<SubAgentDelegationResult> delegation in new[] { a, b, c })
            {
                SubAgentDelegationResult result = await delegation;
                Assert.AreEqual(SubAgentErrorCode.PlanCancelled, result.ErrorCode);
            }

            Assert.AreEqual(2, runner.Requests.Count, "The waiting delegation never started.");
            OrchestrationPlanSnapshot snapshot = plan.Snapshot();
            Assert.AreEqual(OrchestrationPlanState.Cancelled, snapshot.State);
            Assert.AreEqual(OrchestrationStopReason.PlayerStop, snapshot.StopReason);
            Assert.IsNull(session.ActivePlan);
            Assert.AreEqual(0, snapshot.Budget.CallsStarted - snapshot.Budget.CallsCompleted,
                "No reservation is left behind.");
            Assert.AreEqual("stopped 0/3", OrchestrationStatusText.FormatLine(session.Current));
        }

        [Test]
        public async Task StopInOneConversation_LeavesAnotherConversationRunning()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            using OrchestratorSessionRegistry sessions = new(new OrchestratorModeOptions(),
                new SubAgentRegistry(new[] { SubAgentTestSupport.Builder() }), runner);
            OrchestratorSession first = sessions.GetOrCreate("chat-a");
            OrchestratorSession second = sessions.GetOrCreate("chat-b");
            first.BeginPlan("goal a", SubAgentTestSupport.Parent("trace-a", "scope-a"));
            second.BeginPlan("goal b", SubAgentTestSupport.Parent("trace-b", "scope-b"));
            Task<SubAgentDelegationResult> a = SubAgentTestSupport.Delegate(first, "A");
            Task<SubAgentDelegationResult> b = SubAgentTestSupport.Delegate(second, "B");

            first.Stop();

            Assert.IsTrue(runner.Tokens[0].IsCancellationRequested);
            Assert.IsFalse(runner.Tokens[1].IsCancellationRequested, "Two conversations never share a scope.");
            runner.Held[1].TrySetResult(SubAgentTestSupport.Answer("b"));
            Assert.IsTrue((await b).Ok);
            Assert.AreEqual(SubAgentErrorCode.PlanCancelled, (await a).ErrorCode);
            Assert.AreEqual(OrchestrationPlanState.Running, second.ActivePlan.State);
        }

        [Test]
        public async Task CompletePlan_StopsARunTheLeadLeftBehind()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());
            Task<SubAgentDelegationResult> orphan = SubAgentTestSupport.Delegate(session, "A");

            session.CompletePlan(OrchestrationPlanState.Completed);

            Assert.IsTrue(runner.Tokens[0].IsCancellationRequested);
            Assert.AreEqual(SubAgentErrorCode.PlanCancelled, (await orphan).ErrorCode);
            Assert.AreEqual(OrchestrationPlanState.Completed, plan.State);
            Assert.AreEqual(OrchestrationStopReason.PlanCompleted, plan.Scope.StopReason);
        }

        [Test]
        public async Task LeadInvocationCancelled_RethrowsCancellation()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());
            using CancellationTokenSource leadCall = new();
            Task<SubAgentDelegationResult> running =
                SubAgentTestSupport.Delegate(session, "A", cancellationToken: leadCall.Token);

            leadCall.Cancel();
            bool threwCancellation = false;
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
                threwCancellation = true;
            }

            Assert.IsTrue(threwCancellation);
            Assert.IsTrue(runner.Tokens[0].IsCancellationRequested);
            Assert.AreEqual(PlanTaskState.Cancelled, plan.Snapshot().Tasks[0].State);
        }

        [Test]
        public async Task Deadline_EndsTheRunWithATypedReason_AndThePlanGoesOn()
        {
            ManualDelayMarshaler delays = new();
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner, null, delays,
                SubAgentTestSupport.Builder(deadline: TimeSpan.FromSeconds(30)));
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());
            Task<SubAgentDelegationResult> running = SubAgentTestSupport.Delegate(session, "A");
            Assert.AreEqual(1, delays.Delays.Count);
            Assert.AreEqual(30000, delays.Delays[0].Milliseconds);

            delays.ElapseAll();
            SubAgentDelegationResult result = await running;

            Assert.AreEqual(SubAgentErrorCode.DeadlineExceeded, result.ErrorCode);
            Assert.IsTrue(runner.Tokens[0].IsCancellationRequested);
            Assert.AreEqual(PlanTaskState.Failed, plan.Snapshot().Tasks[0].State);
            Assert.AreEqual(OrchestrationPlanState.Running, plan.State);
        }

        [Test]
        public async Task FinishedRun_ReleasesItsDeadline()
        {
            ManualDelayMarshaler delays = new();
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner, null, delays,
                SubAgentTestSupport.Builder(deadline: TimeSpan.FromSeconds(30)));
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "A");
            delays.ElapseAll();

            Assert.IsTrue(result.Ok, result.Message);
            Assert.IsTrue(delays.Delays[0].Token.IsCancellationRequested, "The host delay is released, not left running.");
            Assert.IsFalse(runner.Tokens[0].IsCancellationRequested);
        }

        [Test]
        public async Task WithoutARunningPlan_DelegationIsRefused()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner);

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "Build walls");

            Assert.AreEqual(SubAgentErrorCode.PlanNotActive, result.ErrorCode);
            Assert.AreEqual(0, runner.Requests.Count);
        }

        [Test]
        public async Task UnknownSubAgent_IsRefused_NamingTheKnownOnes()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "Build walls", "painter");

            Assert.AreEqual(SubAgentErrorCode.UnknownSubAgent, result.ErrorCode);
            StringAssert.Contains("builder", result.Message);
            StringAssert.Contains("scripter", result.Message);
            Assert.AreEqual(0, runner.Requests.Count);
        }

        [Test]
        public async Task BlankTask_IsRefusedAsInvalidArguments()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "  ");

            Assert.AreEqual(SubAgentErrorCode.InvalidArguments, result.ErrorCode);
            Assert.AreEqual(0, runner.Requests.Count);
        }

        [Test]
        public async Task BusyTask_IsRefused_WhileItRuns()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());
            string walls = plan.DeclareTask("walls", "builder");
            Task<SubAgentDelegationResult> running = SubAgentTestSupport.Delegate(session, "Build walls", taskId: walls);

            SubAgentDelegationResult again = await SubAgentTestSupport.Delegate(session, "Build walls", taskId: walls);

            Assert.AreEqual(SubAgentErrorCode.TaskBusy, again.ErrorCode);
            runner.Held[0].TrySetResult(SubAgentTestSupport.Answer("done"));
            Assert.IsTrue((await running).Ok);
            Assert.AreEqual(1, plan.Snapshot().Tasks.Count, "The declared task was reused, not duplicated.");
        }

        [Test]
        public void DelegateTool_Contract_SuitsALongDelegation()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            DelegateTaskTool tool = session.DelegateTool;

            Assert.AreEqual(DelegateTaskTool.DefaultName, tool.Name);
            Assert.AreEqual(0, tool.ToolTimeoutMsOverride, "The 30 s default tool budget would cut every delegation.");
            Assert.IsFalse(tool.IsMutating, "Read-only, so the plan's parallelism decides, not the mutation chain.");
            Assert.IsTrue(tool.AllowDuplicates);
            Assert.IsFalse(tool.EndsTurn);
            StringAssert.Contains("- builder: Builds structures in the world.", tool.Description);
            StringAssert.Contains("- scripter:", tool.Description);
            JObject schema = JObject.Parse(tool.ParametersSchema);
            CollectionAssert.AreEquivalent(new[] { "subagent", "task" }, schema["required"].ToObject<string[]>());
            StringAssert.Contains("\"task_id\"", tool.ParametersSchema);
            Assert.AreEqual("delegate_task", tool.CreateAIFunction().Name);
        }

        [Test]
        public void DelegateTool_Description_IsStableUntilTheRegistryChanges()
        {
            SubAgentRegistry registry = new(new[] { SubAgentTestSupport.Builder() });
            OrchestratorSession session = new("c", new OrchestratorModeOptions(), registry,
                new ScriptedSubAgentRunner());
            string first = session.DelegateTool.Description;

            Assert.AreSame(first, session.DelegateTool.Description, "An unchanged registry reuses the same text.");
            registry.Register(SubAgentTestSupport.Scripter());
            StringAssert.Contains("- scripter:", session.DelegateTool.Description);
        }

        private static async Task<SubAgentDelegationResult> RunHeldAsync(
            ScriptedSubAgentRunner runner,
            Func<Task<SubAgentDelegationResult>> start)
        {
            int index = runner.Held.Count;
            Task<SubAgentDelegationResult> delegation = start();
            Assert.IsTrue(await SubAgentTestSupport.EventuallyAsync(() => runner.Held.Count > index));
            runner.Held[index].TrySetResult(SubAgentTestSupport.Answer("ok"));
            return await delegation;
        }
    }
}
