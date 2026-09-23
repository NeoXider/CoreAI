using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="CoreAiChatPanel.ResolveTimeoutMessage"/> and
    /// <see cref="CoreAiChatPanel.ResolveCancelledMessage"/>: which of the two a finished turn reaches.
    /// A turn the caller cancelled used to reach the timeout hook, so a host showed "the teacher is not
    /// responding" and logged <c>reason=timeout</c> for turns that were simply stopped.
    /// </summary>
    public sealed class CoreAiChatPanelResolveTimeoutMessageEditModeTests
    {
        private class PanelProbe : CoreAiChatPanel
        {
            public int TimeoutCalls;
            public int CancelledCalls;
            public int StreamErrorCalls;

            public string Call(bool stopByUser)
            {
                return ResolveTimeoutMessage(stopByUser);
            }

            public string CallCancelled()
            {
                return ResolveCancelledMessage();
            }

            /// <summary>EditMode runs no lifecycle callbacks; this is the destroy a scene change performs.</summary>
            public void SimulateDestroy()
            {
                OnDestroy();
            }

            protected override string ResolveTimeoutMessage(bool stopRequestedByUser)
            {
                TimeoutCalls++;
                return base.ResolveTimeoutMessage(stopRequestedByUser);
            }

            protected override string ResolveCancelledMessage()
            {
                CancelledCalls++;
                return base.ResolveCancelledMessage();
            }

            protected override string ResolveStreamErrorMessage(string chunkError)
            {
                StreamErrorCalls++;
                return base.ResolveStreamErrorMessage(chunkError);
            }
        }

        private sealed class PanelSuppressTimeout : PanelProbe
        {
            protected override string ResolveTimeoutMessage(bool stopRequestedByUser)
            {
                return stopRequestedByUser ? "custom stop" : null;
            }
        }

        [Test]
        public void ResolveTimeoutMessage_Default_TimeoutBranch_UsesConfigOrFallback()
        {
            GameObject go = new();
            PanelProbe panel = go.AddComponent<PanelProbe>();
            Assert.AreEqual("Timeout.", panel.Call(false));
            Object.DestroyImmediate(go);
        }

        [Test]
        public void ResolveTimeoutMessage_Default_StopBranch_DoesNotAppendChatBubble()
        {
            GameObject go = new();
            PanelProbe panel = go.AddComponent<PanelProbe>();
            Assert.IsNull(panel.Call(true));
            Object.DestroyImmediate(go);
        }

        [Test]
        public void ResolveTimeoutMessage_OverrideCanReturnNullForTimeoutBranch()
        {
            GameObject go = new();
            PanelSuppressTimeout panel = go.AddComponent<PanelSuppressTimeout>();
            Assert.IsNull(panel.Call(false));
            Assert.IsFalse(string.IsNullOrEmpty(panel.Call(true)));
            Object.DestroyImmediate(go);
        }

        [Test]
        public void ResolveCancelledMessage_Default_IsSilent()
        {
            GameObject go = new();
            PanelProbe panel = go.AddComponent<PanelProbe>();
            Assert.IsNull(panel.CallCancelled());
            Object.DestroyImmediate(go);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task CallerCancelsExternalTurn_ReachesCancelledHook_NeverTheTimeoutHook(bool streaming)
        {
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, streaming);
            using CancellationTokenSource caller = new();

            Task<string> turn = scope.Panel.SubmitMessageFromExternalAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false },
                caller.Token);
            await orchestrator.Started;
            caller.Cancel();
            string response = await turn;

            Assert.IsNull(response);
            Assert.AreEqual(0, scope.Panel.TimeoutCalls,
                "A turn the caller cancelled is not a timeout and must not produce the timeout bubble.");
            Assert.AreEqual(1, scope.Panel.CancelledCalls);
            Assert.AreEqual(0, scope.Panel.StreamErrorCalls);
            Assert.IsFalse(scope.Panel.IsBusy, "The cancelled turn must release the panel.");
            // WHY: the base class releases every busy flag itself; an override of ResolveCancelledMessage
            // must not have to.
            Assert.IsFalse(GetPanelFlag(scope.Panel, "_isSending"));
            Assert.IsFalse(GetPanelFlag(scope.Panel, "_isStreaming"));
            Assert.IsFalse(GetPanelFlag(scope.Panel, "_stopRequestedByUser"));
            Assert.IsNull(typeof(CoreAiChatPanel)
                    .GetField("_activeRequestCts", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(scope.Panel),
                "The cancelled turn must release its request source.");
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task HostDeadlineOnItsOwnToken_ReachesTheTimeoutHook(bool streaming)
        {
            // WHY: a host deadline armed on the caller's token reads as a stop since 7.44.0 and the host lost its
            // "teacher unavailable" notice. Kept on DeadlineCancellationToken it is a timeout again.
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, streaming);
            using CancellationTokenSource caller = new();
            using CancellationTokenSource deadline = new();

            Task<string> turn = scope.Panel.SubmitMessageFromExternalAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions
                {
                    AppendUserMessageToChat = false,
                    DeadlineCancellationToken = deadline.Token
                },
                caller.Token);
            await orchestrator.Started;
            deadline.Cancel();
            string response = await turn;

            Assert.IsNull(response);
            Assert.AreEqual(1, scope.Panel.TimeoutCalls);
            Assert.AreEqual(0, scope.Panel.CancelledCalls);
            Assert.IsFalse(scope.Panel.IsBusy);
        }

        [Test]
        public async Task ResultApi_HostDeadline_ReportsTimeout_AndCallerStopWins()
        {
            ParkedOrchestrator timedOut = new();
            using (PanelScope scope = NewPanel(timedOut, true))
            using (CancellationTokenSource deadline = new())
            {
                Task<CoreAiChatExternalSubmitResult> turn = scope.Panel.SubmitMessageFromExternalResultAsync(
                    "help payload",
                    new CoreAiChatExternalSubmitOptions
                    {
                        AppendUserMessageToChat = false,
                        DeadlineCancellationToken = deadline.Token
                    });
                await timedOut.Started;
                deadline.Cancel();
                CoreAiChatExternalSubmitResult result = await turn;

                Assert.IsTrue(result.Admitted);
                Assert.AreEqual(LlmErrorCode.Timeout, result.Completion.ErrorCode);
                // WHY: the orchestrator attributes a cancelled turn from these two request tokens, caller first.
                // With the host deadline linked into the token the panel handed the service as the caller's, every
                // host timeout was recorded as a user cancellation while the panel reported a timeout.
                Assert.IsFalse(timedOut.LastRequest.CallerCancellationToken.IsCancellationRequested,
                    "Nobody asked to stop: the caller token the orchestrator sees must still be alive.");
                Assert.IsTrue(timedOut.LastRequest.DeadlineCancellationToken.IsCancellationRequested,
                    "The host deadline must reach the orchestrator as the deadline token.");
            }

            ParkedOrchestrator bothFired = new();
            using (PanelScope scope = NewPanel(bothFired, true))
            using (CancellationTokenSource caller = new())
            using (CancellationTokenSource deadline = new())
            {
                Task<CoreAiChatExternalSubmitResult> turn = scope.Panel.SubmitMessageFromExternalResultAsync(
                    "help payload",
                    new CoreAiChatExternalSubmitOptions
                    {
                        AppendUserMessageToChat = false,
                        DeadlineCancellationToken = deadline.Token
                    },
                    caller.Token);
                await bothFired.Started;
                caller.Cancel();
                deadline.Cancel();
                CoreAiChatExternalSubmitResult result = await turn;

                Assert.AreEqual(LlmErrorCode.Cancelled, result.Completion.ErrorCode,
                    "When the caller has stopped the turn, the deadline firing too does not make it a timeout.");
                Assert.IsTrue(bothFired.LastRequest.CallerCancellationToken.IsCancellationRequested,
                    "The caller's stop must reach the orchestrator through the caller token.");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task ResultApi_HostDeadline_IsRecordedAsADeadlineCancellation_NotAsACallerCancellation(
            bool streaming)
        {
            // WHY: AiOrchestrator records a cancelled turn through AiCancellationAttributionContext.Resolve, which
            // reads AiTaskRequest.CallerCancellationToken first. The panel used to link the host deadline into the
            // token it handed the service as the caller's, so the dashboard counted every host timeout as a user
            // cancellation while the transcript showed the timeout notice.
            InMemoryAiOrchestrationMetrics metrics = new();
            ParkedLlmClient llm = new();
            using PanelScope scope = NewPanelWithRealOrchestrator(llm, metrics, streaming);
            using CancellationTokenSource deadline = new();

            Task<CoreAiChatExternalSubmitResult> turn = scope.Panel.SubmitMessageFromExternalResultAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions
                {
                    AppendUserMessageToChat = false,
                    DeadlineCancellationToken = deadline.Token
                });
            await llm.Started;
            deadline.Cancel();
            CoreAiChatExternalSubmitResult result = await turn;

            Assert.IsTrue(result.Admitted);
            Assert.AreEqual(LlmErrorCode.Timeout, result.Completion.ErrorCode);
            Assert.AreEqual(1, metrics.DeadlineCancelledCompletions,
                "A host deadline at a live caller is a deadline cancellation in the orchestration metrics.");
            // WHY a difference and not zero: a deadline cancellation IS a cancellation in these counters
            // (InMemoryAiOrchestrationMetrics increments both), so what must stay empty is the part of
            // CancelledCompletions no deadline explains - the user cancellations.
            Assert.AreEqual(0, metrics.CancelledCompletions - metrics.DeadlineCancelledCompletions,
                "The metrics must not count the host's timeout as a user cancellation.");
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task ResultApi_CallerStop_IsRecordedAsACancellation_EvenWhenTheDeadlineFiredToo(bool streaming)
        {
            InMemoryAiOrchestrationMetrics metrics = new();
            ParkedLlmClient llm = new();
            using PanelScope scope = NewPanelWithRealOrchestrator(llm, metrics, streaming);
            using CancellationTokenSource caller = new();
            using CancellationTokenSource deadline = new();

            Task<CoreAiChatExternalSubmitResult> turn = scope.Panel.SubmitMessageFromExternalResultAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions
                {
                    AppendUserMessageToChat = false,
                    DeadlineCancellationToken = deadline.Token
                },
                caller.Token);
            await llm.Started;
            caller.Cancel();
            deadline.Cancel();
            CoreAiChatExternalSubmitResult result = await turn;

            Assert.AreEqual(LlmErrorCode.Cancelled, result.Completion.ErrorCode);
            Assert.AreEqual(1, metrics.CancelledCompletions, "The caller's stop wins over the deadline that raced it.");
            Assert.AreEqual(0, metrics.DeadlineCancelledCompletions);
        }

        [Test]
        public async Task StringApi_DeadlineAlreadyElapsed_ShowsTheTimeout_AndStartsNoTurn()
        {
            // WHY: the typed API rejected an elapsed deadline before admission, but the string API had no outcome
            // to reject into and went on - user bubble, OnUserMessageSent, a turn the orchestrator recorded as
            // unanswered - and only then showed the timeout bubble.
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, true);
            using CancellationTokenSource deadline = new();
            deadline.Cancel();
            int userMessagesSent = 0;
            scope.Panel.OnUserMessageSent += _ => userMessagesSent++;

            string response = await scope.Panel.SubmitMessageFromExternalAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions { DeadlineCancellationToken = deadline.Token });

            Assert.IsNull(response);
            Assert.AreEqual(0, userMessagesSent, "An elapsed deadline admits nothing: no user message is recorded.");
            Assert.AreEqual(0, orchestrator.Calls, "The service must not be called for a turn that is already over.");
            Assert.AreEqual(1, scope.Panel.TimeoutCalls, "The host's timeout is presented exactly once.");
            Assert.AreEqual(0, scope.Panel.CancelledCalls);
            Assert.IsFalse(scope.Panel.IsBusy);
        }

        [Test]
        public async Task StringApi_CallerAlreadyCancelled_DoesNothing()
        {
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, true);
            using CancellationTokenSource caller = new();
            caller.Cancel();
            int userMessagesSent = 0;
            scope.Panel.OnUserMessageSent += _ => userMessagesSent++;

            string response = await scope.Panel.SubmitMessageFromExternalAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions(),
                caller.Token);

            Assert.IsNull(response);
            Assert.AreEqual(0, userMessagesSent);
            Assert.AreEqual(0, orchestrator.Calls);
            Assert.AreEqual(0, scope.Panel.TimeoutCalls);
            Assert.AreEqual(0, scope.Panel.CancelledCalls, "A caller that already cancelled asked for silence.");
            Assert.IsFalse(scope.Panel.IsBusy);
        }

        [Test]
        public async Task DisposedDeadlineSource_NeverLeavesTheRequestSourcePublished()
        {
            // WHY: the request source was published to _activeRequestCts before the turn's try, and the host
            // deadline was linked to it right after. On a runtime where linking to a disposed source throws, the
            // turn escaped with that source neither disposed nor cleared and the panel never recovered. Whether the
            // runtime throws or lets the turn park, everything the turn owns must be released when it ends.
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, true);
            using CancellationTokenSource caller = new();
            CancellationTokenSource deadline = new();
            CancellationToken disposedDeadline = deadline.Token;
            deadline.Dispose();

            try
            {
                // WHY: on the throwing runtime the turn ends as a logged provider failure; the log is not under test.
                LogAssert.ignoreFailingMessages = true;
                Task<string> turn = scope.Panel.SubmitMessageFromExternalAsync(
                    "help payload",
                    new CoreAiChatExternalSubmitOptions
                    {
                        AppendUserMessageToChat = false,
                        DeadlineCancellationToken = disposedDeadline
                    },
                    caller.Token);
                if (await Task.WhenAny(turn, orchestrator.Started) == orchestrator.Started)
                {
                    caller.Cancel();
                }

                Assert.IsNull(await turn);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            Assert.IsNull(GetActiveRequestSource(scope.Panel), "The turn's finally must release the request source.");
            Assert.IsFalse(scope.Panel.IsBusy);
            Assert.AreEqual(0, scope.Panel.TimeoutCalls, "A dead deadline source is not a timeout.");
        }

        [Test]
        public async Task StopOrAbandon_AfterDestroy_ArmsNoRootSource()
        {
            // WHY: StopAgent and AbandonCurrentTurn replaced the root source unconditionally. After OnDestroy had
            // released it they allocated a fresh one on a dead panel that nothing would ever cancel or dispose.
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, true);
            Task<string> turn = scope.Panel.SubmitMessageFromExternalAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            await orchestrator.Started;
            scope.Panel.SimulateDestroy();
            Assert.IsNull(GetRootSource(scope.Panel), "OnDestroy releases the root source.");

            scope.Panel.StopAgent();
            Assert.IsNull(GetRootSource(scope.Panel), "A stop after destroy has nothing left to arm.");
            Assert.IsFalse(scope.Panel.AbandonCurrentTurn(), "After destroy there is no turn left to abandon.");
            Assert.IsNull(GetRootSource(scope.Panel));

            Assert.IsNull(await turn);
            Assert.IsFalse(scope.Panel.AbandonCurrentTurn());
            Assert.IsNull(GetRootSource(scope.Panel));
            Assert.IsNull(GetActiveRequestSource(scope.Panel));
            Assert.IsFalse(scope.Panel.IsBusy);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task StopThenDestroy_LeavesNoSourceBehind_AndReachesNoHook(bool streaming)
        {
            // WHY: a stop followed by a scene change is the ordinary way a lesson ends. The stopped turn is stale,
            // so it reaches no hook; the stop released the request source and OnDestroy the root, so neither may
            // survive. Unity fails this test on any unexpected error log.
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, streaming);
            Task<string> turn = scope.Panel.SubmitMessageFromExternalAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            await orchestrator.Started;

            scope.Panel.StopAgent();
            scope.Panel.SimulateDestroy();
            Assert.IsNull(await turn);

            Assert.AreEqual(0, scope.Panel.TimeoutCalls);
            Assert.AreEqual(0, scope.Panel.CancelledCalls);
            Assert.AreEqual(0, scope.Panel.StreamErrorCalls);
            Assert.IsNull(GetActiveRequestSource(scope.Panel));
            Assert.IsNull(GetRootSource(scope.Panel));
            Assert.IsFalse(scope.Panel.IsBusy);
        }

        [Test]
        public async Task StopThenResend_SecondTurnCompletesNormally_OnAFreshRootSource()
        {
            // WHY: stop + resend is the learner's usual retry. The stopped turn unwinds as stale while the new one
            // runs on the root source the stop replaced; the first must reach no hook and leave no busy state, the
            // second must answer.
            ParkedThenReplyOrchestrator orchestrator = new("second answer");
            using PanelScope scope = NewPanel(orchestrator, true);
            Task<string> first = scope.Panel.SubmitMessageFromExternalAsync(
                "first question",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            await orchestrator.Started;
            CancellationTokenSource rootBefore = GetRootSource(scope.Panel);
            Assert.IsNotNull(rootBefore);

            scope.Panel.StopAgent();
            Task<string> second = scope.Panel.SubmitMessageFromExternalAsync(
                "second question",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            string response = await second;
            Assert.IsNull(await first);

            StringAssert.Contains("second answer", response);
            Assert.AreEqual(0, scope.Panel.TimeoutCalls);
            Assert.AreEqual(0, scope.Panel.CancelledCalls);
            Assert.AreEqual(0, scope.Panel.StreamErrorCalls);
            Assert.IsFalse(scope.Panel.IsBusy);
            Assert.IsNull(GetActiveRequestSource(scope.Panel));
            CancellationTokenSource rootAfter = GetRootSource(scope.Panel);
            Assert.IsNotNull(rootAfter);
            Assert.AreNotSame(rootBefore, rootAfter, "The stop replaced the root source the first turn was linked to.");
            Assert.Throws<ObjectDisposedException>(() => { _ = rootBefore.Token; }, "The replaced root was disposed.");
            Assert.IsFalse(rootAfter.IsCancellationRequested);
        }

        [Test]
        public async Task StopWhileDisabled_ArmsNoRootSource_TheNextTurnCreatesItLazily()
        {
            // WHY: a stop on a disabled panel (a host that keeps the turn alive across OnDisable and then gives up
            // on it) replaced the root source too, on a lifecycle that was not there to own it. The root now stays
            // absent until the next turn asks GetOrCreateCancellationTokenSource for one.
            ParkedThenReplyOrchestrator orchestrator = new("answer after re-enable");
            using PanelScope scope = NewPanel(orchestrator, true);
            Task<string> first = scope.Panel.SubmitMessageFromExternalAsync(
                "first question",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            await orchestrator.Started;

            SetLifecycleActive(scope.Panel, false);
            scope.Panel.StopAgent();
            Assert.IsNull(await first);
            Assert.IsNull(GetRootSource(scope.Panel), "A disabled panel has no lifecycle to own a new root source.");
            Assert.IsFalse(scope.Panel.IsBusy);

            SetLifecycleActive(scope.Panel, true);
            string response = await scope.Panel.SubmitMessageFromExternalAsync(
                "second question",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            StringAssert.Contains("answer after re-enable", response);
            Assert.IsNotNull(GetRootSource(scope.Panel), "The next turn created the root source it needed.");
            Assert.AreEqual(0, scope.Panel.TimeoutCalls);
            Assert.AreEqual(0, scope.Panel.CancelledCalls);
            Assert.IsFalse(scope.Panel.IsBusy);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task UiStop_ReachesNeitherTheTimeoutNorTheCancelledHook(bool streaming)
        {
            // WHY: 7.44.0 documents a user stop as a superseded turn that reaches neither ResolveTimeoutMessage nor
            // ResolveCancelledMessage - a host counting hook calls never sees its own stops. StopAgent() is the
            // Stop button's path (StopActiveGeneration).
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, streaming);
            Task turn = (Task)typeof(CoreAiChatPanel)
                .GetMethod("SendToAIFromUiAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scope.Panel, new object[] { "typed question" });
            await orchestrator.Started;

            scope.Panel.StopAgent();
            await turn;

            Assert.AreEqual(0, scope.Panel.TimeoutCalls, "A user stop is not a timeout.");
            Assert.AreEqual(0, scope.Panel.CancelledCalls, "A user stop is a superseded turn, not a cancelled one.");
            Assert.AreEqual(0, scope.Panel.StreamErrorCalls);
            Assert.IsFalse(scope.Panel.IsBusy);
            Assert.IsNull(GetActiveRequestSource(scope.Panel));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task DestroyMidTurn_TheTurnAloneDisposesItsRequestSource(bool streaming)
        {
            // WHY: 7.44.1 made the turn's finally the single owner of the request source. OnDestroy only cancels it;
            // disposing there turned the unwinding turn's cancellation into ObjectDisposedException.
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, streaming);
            Task<string> turn = scope.Panel.SubmitMessageFromExternalAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            await orchestrator.Started;
            CancellationTokenSource active = GetActiveRequestSource(scope.Panel);
            Assert.IsNotNull(active);

            scope.Panel.SimulateDestroy();
            Assert.IsTrue(active.IsCancellationRequested, "OnDestroy cancels the active request.");
            if (!turn.IsCompleted)
            {
                Assert.DoesNotThrow(() => { _ = active.Token; },
                    "OnDestroy must not dispose the request source: the turn is still unwinding on it.");
            }

            Assert.IsNull(await turn);
            Assert.Throws<ObjectDisposedException>(() => { _ = active.Token; },
                "The turn's finally disposes the request source it created.");
            Assert.IsNull(GetActiveRequestSource(scope.Panel));
            Assert.AreEqual(1, scope.Panel.CancelledCalls);
        }

        private static CancellationTokenSource GetActiveRequestSource(CoreAiChatPanel panel)
        {
            return (CancellationTokenSource)typeof(CoreAiChatPanel)
                .GetField("_activeRequestCts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel);
        }

        private static CancellationTokenSource GetRootSource(CoreAiChatPanel panel)
        {
            return (CancellationTokenSource)typeof(CoreAiChatPanel)
                .GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel);
        }

        /// <summary>EditMode runs no lifecycle callbacks; this is the flag OnEnable / OnDisable flip.</summary>
        private static void SetLifecycleActive(CoreAiChatPanel panel, bool active)
        {
            typeof(CoreAiChatPanel)
                .GetField("_lifecycleActive", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, active);
        }

        [Test]
        public async Task ResultApi_DeadlineAlreadyElapsed_IsRejectedAsTimeout()
        {
            using PanelScope scope = NewPanel(new ParkedOrchestrator(), true);
            using CancellationTokenSource deadline = new();
            deadline.Cancel();

            CoreAiChatExternalSubmitResult result = await scope.Panel.SubmitMessageFromExternalResultAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions
                {
                    AppendUserMessageToChat = false,
                    DeadlineCancellationToken = deadline.Token
                });

            Assert.IsFalse(result.Admitted);
            Assert.AreEqual(CoreAiChatExternalSubmitRejection.Cancelled, result.Rejection);
            Assert.AreEqual(LlmErrorCode.Timeout, result.Completion.ErrorCode);
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public async Task PanelDestroyedMidTurn_EndsAsACancellation_WithoutADisposedSourceError(
            bool streaming,
            bool external)
        {
            // WHY: 7.44.0 read the request source's Token in the turn's catch, and OnDestroy had already
            // disposed that source - a scene change mid-turn became ObjectDisposedException, logged as an error
            // (Unity fails this test on any unexpected error log) instead of an ordinary cancellation.
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, streaming);

            Task turn = external
                ? scope.Panel.SubmitMessageFromExternalAsync(
                    "help payload",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false })
                : (Task)typeof(CoreAiChatPanel)
                    .GetMethod("SendToAIFromUiAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(scope.Panel, new object[] { "typed question" });
            await orchestrator.Started;
            scope.Panel.SimulateDestroy();
            await turn;

            Assert.AreEqual(1, scope.Panel.CancelledCalls, "Destroying the panel stops the turn: a cancellation.");
            Assert.AreEqual(0, scope.Panel.TimeoutCalls);
            Assert.AreEqual(0, scope.Panel.StreamErrorCalls);
            Assert.IsFalse(scope.Panel.IsBusy);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task ResultApi_PanelDestroyedMidTurn_ReportsCancelled(bool streaming)
        {
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, streaming);

            Task<CoreAiChatExternalSubmitResult> turn = scope.Panel.SubmitMessageFromExternalResultAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            await orchestrator.Started;
            scope.Panel.SimulateDestroy();
            CoreAiChatExternalSubmitResult result = await turn;

            Assert.IsTrue(result.Admitted);
            Assert.AreEqual(LlmErrorCode.Cancelled, result.Completion.ErrorCode,
                "Not ProviderError: the turn was stopped, it did not fail.");
            StringAssert.DoesNotContain("disposed", result.Completion.Error ?? "");
        }

        private static bool GetPanelFlag(CoreAiChatPanel panel, string fieldName)
        {
            return (bool)typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task LibraryTimeout_ReachesTimeoutHookOnce(bool streaming)
        {
            using PanelScope scope = NewPanel(new ThrowingOrchestrator(new LlmOperationTimeoutException()), streaming);

            string response = await scope.Panel.SubmitMessageFromExternalAsync(
                "question",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            Assert.IsNull(response);
            Assert.AreEqual(1, scope.Panel.TimeoutCalls);
            Assert.AreEqual(0, scope.Panel.CancelledCalls);
        }

        [Test]
        public async Task CancellationFromElsewhere_WithLiveCallerToken_IsNotATimeout()
        {
            // WHY: CoreAi.StopAgent or a cancellation scope stops the orchestrator without touching the
            // panel's token; the resulting bare OperationCanceledException is still a cancellation.
            using PanelScope scope = NewPanel(new ThrowingOrchestrator(new OperationCanceledException()), true);

            string response = await scope.Panel.SubmitMessageFromExternalAsync(
                "question",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            Assert.IsNull(response);
            Assert.AreEqual(0, scope.Panel.TimeoutCalls);
            Assert.AreEqual(1, scope.Panel.CancelledCalls);
        }

        [Test]
        public async Task TerminalTimeoutChunk_ReachesTimeoutHook_NotTheStreamErrorHook()
        {
            using PanelScope scope = NewPanel(new ChunkOrchestrator(new LlmStreamChunk
            {
                IsDone = true,
                Error = "LLM request timed out.",
                ErrorCode = LlmErrorCode.Timeout
            }), true);

            string response = await scope.Panel.SubmitMessageFromExternalAsync(
                "question",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            Assert.IsNull(response);
            Assert.AreEqual(1, scope.Panel.TimeoutCalls);
            Assert.AreEqual(0, scope.Panel.StreamErrorCalls);
            Assert.AreEqual(0, scope.Panel.CancelledCalls);
        }

        [Test]
        public async Task TerminalProviderErrorChunk_StillReachesTheStreamErrorHook()
        {
            using PanelScope scope = NewPanel(new ChunkOrchestrator(new LlmStreamChunk
            {
                IsDone = true,
                Error = "HTTP 500",
                ErrorCode = LlmErrorCode.ProviderError
            }), true);

            try
            {
                // WHY: a provider failure is logged as an error on purpose; the log line is not under test.
                LogAssert.ignoreFailingMessages = true;
                await scope.Panel.SubmitMessageFromExternalAsync(
                    "question",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            Assert.AreEqual(1, scope.Panel.StreamErrorCalls);
            Assert.AreEqual(0, scope.Panel.TimeoutCalls);
            Assert.AreEqual(0, scope.Panel.CancelledCalls);
        }

        [Test]
        public async Task ResultApi_CallerCancellation_ReportsCancelledNotTimeout()
        {
            ParkedOrchestrator orchestrator = new();
            using PanelScope scope = NewPanel(orchestrator, true);
            using CancellationTokenSource caller = new();

            Task<CoreAiChatExternalSubmitResult> turn = scope.Panel.SubmitMessageFromExternalResultAsync(
                "help payload",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false },
                caller.Token);
            await orchestrator.Started;
            caller.Cancel();
            CoreAiChatExternalSubmitResult result = await turn;

            Assert.IsTrue(result.Admitted);
            Assert.IsFalse(result.Completion.Ok);
            Assert.AreEqual(LlmErrorCode.Cancelled, result.Completion.ErrorCode);
        }

        [Test]
        public async Task ResultApi_LibraryTimeout_ReportsTimeout()
        {
            using PanelScope scope = NewPanel(new ThrowingOrchestrator(new LlmOperationTimeoutException()), true);

            CoreAiChatExternalSubmitResult result = await scope.Panel.SubmitMessageFromExternalResultAsync(
                "question",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            Assert.IsTrue(result.Admitted);
            Assert.AreEqual(LlmErrorCode.Timeout, result.Completion.ErrorCode);
        }

        private readonly struct PanelScope : IDisposable
        {
            public readonly GameObject Go;
            public readonly PanelProbe Panel;

            public PanelScope(GameObject go, PanelProbe panel)
            {
                Go = go;
                Panel = panel;
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Go);
            }
        }

        private static PanelScope NewPanel(IAiOrchestrationService orchestrator, bool streaming)
        {
            GameObject go = new("CoreAiChatPanel_Interruption_Test");
            PanelProbe panel = go.AddComponent<PanelProbe>();
            panel.SetActorIdentityProvider(new LocalActorIdentityProvider("interruption-panel-test"));
            // WHY: plain EditMode tests do not run the MonoBehaviour lifecycle; see CoreAiChatPanelBusyApiEditModeTests.
            SetLifecycleActive(panel, true);
            panel.ChatService = new CoreAiChatService(
                orchestrator,
                settings: new StubSettings { EnableStreaming = streaming });
            return new PanelScope(go, panel);
        }

        /// <summary>
        /// A panel over the real <see cref="AiOrchestrator"/>, so the outcome it records into
        /// <paramref name="metrics"/> is the production attribution, not a fake's.
        /// </summary>
        private static PanelScope NewPanelWithRealOrchestrator(
            ILlmClient llm,
            IAiOrchestrationMetrics metrics,
            bool streaming)
        {
            const string roleId = "interruption-metrics";
            StubSettings settings = new() { EnableStreaming = streaming };
            AgentMemoryPolicy policy = new();
            policy.ConfigureChatHistory(roleId, true, 8192, false, 10);
            policy.DisableMemoryTool(roleId);
            policy.SetToolsForRole(roleId, Array.Empty<ILlmTool>());
            LocalActorIdentityProvider identity = new("interruption-panel-test");
            AiOrchestrator orchestrator = new(
                new SoloAuthorityHost(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policy, settings),
                new NullAgentMemoryStore(), policy, null, metrics, settings, identity);

            GameObject go = new("CoreAiChatPanel_Interruption_Metrics_Test");
            PanelProbe panel = go.AddComponent<PanelProbe>();
            panel.SetRuntimeOptions(new CoreAiChatOptions { RoleId = roleId, EnableStreaming = true });
            panel.SetActorIdentityProvider(identity);
            SetLifecycleActive(panel, true);
            panel.ChatService = new CoreAiChatService(orchestrator, policy, settings);
            return new PanelScope(go, panel);
        }

        private sealed class StubSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix { get; set; } = string.Empty;
            public float Temperature { get; set; } = 0.3f;
            public int ContextWindowTokens => 8192;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 0;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming { get; set; } = true;
        }

        /// <summary>Starts a turn and waits until it is cancelled; <see cref="Started"/> marks the wait.</summary>
        private sealed class ParkedOrchestrator : IAiOrchestrationService, IAiTaskResultService
        {
            private readonly TaskCompletionSource<bool> _started =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Started => _started.Task;

            /// <summary>How many turns reached the orchestrator at all.</summary>
            public int Calls { get; private set; }

            /// <summary>The request of the last turn, with the tokens the service put on it.</summary>
            public AiTaskRequest LastRequest { get; private set; }

            public async Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                Observe(request);
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            }

            public async Task<LlmCompletionResult> RunTaskResultAsync(
                AiTaskRequest request,
                CancellationToken ct = default)
            {
                Observe(request);
                await Task.Delay(Timeout.Infinite, ct);
                return new LlmCompletionResult { Ok = true, Content = "unreachable" };
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                Calls++;
                LastRequest = request;
                yield return new LlmStreamChunk { Text = "partial" };
                _started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }

            private void Observe(AiTaskRequest request)
            {
                Calls++;
                LastRequest = request;
                _started.TrySetResult(true);
            }
        }

        /// <summary>Parks the first turn until it is cancelled; every later turn answers with <c>reply</c>.</summary>
        private sealed class ParkedThenReplyOrchestrator : IAiOrchestrationService
        {
            private readonly string _reply;
            private readonly TaskCompletionSource<bool> _started =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _calls;

            public ParkedThenReplyOrchestrator(string reply)
            {
                _reply = reply;
            }

            public Task Started => _started.Task;

            public async Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                if (++_calls > 1)
                {
                    return _reply;
                }

                _started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                if (++_calls > 1)
                {
                    await Task.Yield();
                    yield return new LlmStreamChunk { Text = _reply };
                    yield return new LlmStreamChunk { IsDone = true };
                    yield break;
                }

                yield return new LlmStreamChunk { Text = "partial" };
                _started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        /// <summary>The model that never answers: parks until its token is cancelled; <see cref="Started"/> marks the wait.</summary>
        private sealed class ParkedLlmClient : ILlmClient
        {
            private readonly TaskCompletionSource<bool> _started =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Started => _started.Task;

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                _started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new LlmCompletionResult { Ok = true, Content = "unreachable" };
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                _started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield return new LlmStreamChunk { Text = "unreachable", IsDone = true };
            }
        }

        private sealed class TestSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        private sealed class TestTelemetry : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot()
            {
                return new GameSessionSnapshot();
            }
        }

        private sealed class NullSys : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = null;
                return false;
            }
        }

        private sealed class NullUsr : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = null;
                return false;
            }
        }

        private sealed class ThrowingOrchestrator : IAiOrchestrationService, IAiTaskResultService
        {
            private readonly Exception _failure;

            public ThrowingOrchestrator(Exception failure)
            {
                _failure = failure;
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                return Task.FromException<string>(_failure);
            }

            public Task<LlmCompletionResult> RunTaskResultAsync(
                AiTaskRequest request,
                CancellationToken ct = default)
            {
                return Task.FromException<LlmCompletionResult>(_failure);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                await Task.Yield();
                throw _failure;
#pragma warning disable CS0162 // unreachable code: needed only so the compiler treats this as an iterator.
                yield break;
#pragma warning restore CS0162
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class ChunkOrchestrator : IAiOrchestrationService, IAiTaskResultService
        {
            private readonly LlmStreamChunk _terminal;

            public ChunkOrchestrator(LlmStreamChunk terminal)
            {
                _terminal = terminal;
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(string.Empty);
            }

            public Task<LlmCompletionResult> RunTaskResultAsync(
                AiTaskRequest request,
                CancellationToken ct = default)
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = false,
                    ErrorCode = _terminal.ErrorCode,
                    Error = _terminal.Error
                });
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                await Task.Yield();
                yield return _terminal;
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }
    }
}
