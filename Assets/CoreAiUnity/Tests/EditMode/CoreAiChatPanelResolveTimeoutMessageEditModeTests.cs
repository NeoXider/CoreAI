using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
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
            }
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
            typeof(CoreAiChatPanel)
                .GetField("_lifecycleActive", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, true);
            panel.ChatService = new CoreAiChatService(
                orchestrator,
                settings: new StubSettings { EnableStreaming = streaming });
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

            public async Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                _started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            }

            public async Task<LlmCompletionResult> RunTaskResultAsync(
                AiTaskRequest request,
                CancellationToken ct = default)
            {
                _started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                return new LlmCompletionResult { Ok = true, Content = "unreachable" };
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                yield return new LlmStreamChunk { Text = "partial" };
                _started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
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
