using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Real Unity lifecycle coverage for CoreAiChatPanel ownership and stale-turn teardown.
    /// These tests live in a PlayMode assembly so lifecycle callbacks are native and never require
    /// EditMode TestRunner Enter/ExitPlayMode roundtrips.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatPanelLifecyclePlayModeTests
    {
        [UnityTest]
        public IEnumerator DisabledPanel_ReleasesStreamingClassBeforeDroppingUiReferences()
        {
            using LifecyclePanelCtx ctx = NewLifecyclePanel(true);
            InitializeLifecyclePanel(ctx);
            FakeGatedStreamOrchestrator orchestrator = new();
            try
            {
                ScrollView scroll = CurrentMessageScroll(ctx.Panel);
                ctx.Panel.ChatService = new CoreAiChatService(
                    orchestrator,
                    settings: new StubSettings { EnableStreaming = true });

                Task<string> turnTask = ctx.Panel.SubmitMessageFromExternalAsync(
                    "вопрос",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

                yield return WaitForTask(orchestrator.BubbleRendered, "streaming bubble before disable");
                Assert.AreEqual(1, CountActiveStreamingBubbles(scroll),
                    "precondition: a parented bubble must be actively streaming before disable.");

                ctx.Go.SetActive(false);

                Assert.AreEqual(0, CountActiveStreamingBubbles(scroll),
                    "OnDisable must remove the active class before ResetUiReferences loses the label.");

                orchestrator.Release();
                yield return WaitForTask(turnTask, "disabled turn cleanup");
            }
            finally
            {
                orchestrator.Release();
            }
        }

        /// <summary>
        /// Real lifecycle regression for disable/re-enable ownership. The first stream deliberately ignores
        /// cancellation and fails only after a real successor stream has opened its bubble in a replacement
        /// tree. Its late unwind must own nothing in the new lifecycle.
        /// </summary>
        [UnityTest]
        public IEnumerator DisableThenImmediateEnable_LateStreamCannotTouchSuccessorLifecycle()
        {
            LifecyclePanelCtx ctx = NewLifecyclePanel(true);
            InitializeLifecyclePanel(ctx);
            FakeLifecycleOverlapStreamOrchestrator orchestrator = new();
            Task<string> oldTurn = null;
            Task<string> successorTurn = null;
            try
            {
                ctx.Panel.ChatService = new CoreAiChatService(
                    orchestrator,
                    settings: new StubSettings { EnableStreaming = true });

                oldTurn = ctx.Panel.SubmitMessageFromExternalAsync(
                    "old",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
                while (!orchestrator.FirstBubbleRendered.IsCompleted)
                {
                    yield return null;
                }

                ScrollView oldScroll = CurrentMessageScroll(ctx.Panel);
                Assert.AreEqual(1, CountActiveStreamingBubbles(oldScroll),
                    "precondition: the old lifecycle must own one active streaming bubble");
                int oldTurnGeneration = ctx.Panel.CurrentTurnGeneration;

                ctx.Go.SetActive(false);

                Assert.AreEqual(oldTurnGeneration + 1, ctx.Panel.CurrentTurnGeneration,
                    "OnDisable must move generation before cancelling the old request");
                Assert.IsFalse(ctx.Panel.IsBusy,
                    "lifecycle teardown must unlock locally because the stale turn may no longer reset busy");
                Assert.AreEqual(0, CountActiveStreamingBubbles(oldScroll),
                    "the old tree must not keep advertising a live stream after disable");

                ctx.Go.SetActive(true);
                ctx.ReplaceChatTree();
                ctx.Panel.ChatService = new CoreAiChatService(
                    orchestrator,
                    settings: new StubSettings { EnableStreaming = true });
                ScrollView successorScroll = CurrentMessageScroll(ctx.Panel);
                Assert.AreNotSame(oldScroll, successorScroll,
                    "the re-enabled panel must bind the replacement lifecycle tree");

                // WHY: seed state owned by the replacement lifecycle BEFORE another turn can increment
                // generation. This makes the disable bump itself, not a later send, the only stale barrier.
                Label successorBubble = new("replacement lifecycle bubble");
                successorBubble.AddToClassList(CoreAiChatPanel.StreamingActiveUssClassName);
                successorScroll.Add(successorBubble);
                SetField(ctx.Panel, "_streamingLabel", successorBubble);
                SetFlag(ctx.Panel, "_isSending", true);
                SetFlag(ctx.Panel, "_isStreaming", true);
                InvokeUpdateSendButton(ctx.Panel);
                Assert.IsTrue(ctx.Panel.IsBusy, "precondition: the replacement lifecycle owns busy state");
                Assert.AreEqual(1, CountActiveStreamingBubbles(successorScroll));

                yield return DrainAndResetAutoFocusAttempts(ctx.Panel);
                int successorChildCount = successorScroll.childCount;

                // WHY: the late provider failure stays logged for diagnostics, but must not render that
                // error or run old cleanup against the successor lifecycle.
                LogAssert.ignoreFailingMessages = true;
                orchestrator.ReleaseFirstWithFailure();
                while (!oldTurn.IsCompleted)
                {
                    yield return null;
                }

                if (oldTurn.IsFaulted)
                {
                    throw oldTurn.Exception.GetBaseException();
                }

                yield return null;
                Assert.IsNull(oldTurn.Result, "a disabled lifecycle's failed turn must resolve as stale");
                Assert.IsTrue(ctx.Panel.IsBusy,
                    "the old finally must not reset the successor's busy ownership");
                Assert.AreEqual(successorChildCount, successorScroll.childCount,
                    "the late error/timeout path must not append a bubble to the replacement tree");
                Assert.AreSame(successorBubble, GetField<Label>(ctx.Panel, "_streamingLabel"));
                Assert.IsTrue(successorBubble.ClassListContains(CoreAiChatPanel.StreamingActiveUssClassName),
                    "the old stream teardown must not release the successor bubble");
                Assert.AreEqual("replacement lifecycle bubble", successorBubble.text,
                    "the old stream must not append into the successor bubble");
                Assert.AreEqual(0, ctx.Panel.AutoFocusAttemptCount,
                    "the old turn must not schedule focus into the replacement tree");

                LogAssert.ignoreFailingMessages = false;
                ctx.Panel.ResetBusyStateWithoutCancellation();
                successorScroll.Clear();
                int successorGenerationBefore = ctx.Panel.CurrentTurnGeneration;
                successorTurn = ctx.Panel.SubmitMessageFromExternalAsync(
                    "successor",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
                while (!orchestrator.SecondBubbleRendered.IsCompleted)
                {
                    yield return null;
                }

                Assert.AreEqual(successorGenerationBefore + 1, ctx.Panel.CurrentTurnGeneration,
                    "the first real turn in the replacement lifecycle must get the next generation");
                Assert.IsTrue(ctx.Panel.IsBusy);
                Assert.AreEqual("successor response", successorScroll
                    .Query<Label>(className: CoreAiChatPanel.StreamingActiveUssClassName)
                    .First().text);
                orchestrator.ReleaseSecond();
                while (!successorTurn.IsCompleted)
                {
                    yield return null;
                }

                if (successorTurn.IsFaulted)
                {
                    throw successorTurn.Exception.GetBaseException();
                }

                Assert.AreEqual("successor response", successorTurn.Result);
                Assert.IsFalse(ctx.Panel.IsBusy);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                orchestrator.ReleaseFirstWithFailure();
                orchestrator.ReleaseSecond();
                ctx.Dispose();
            }
        }

        /// <summary>
        /// Buffered replies have their own await path. A provider that ignores cancellation must still be
        /// generation-gated before it hides typing or appends its late answer into a re-enabled panel.
        /// </summary>
        [UnityTest]
        public IEnumerator DisableThenImmediateEnable_LateNonStreamingReplyCannotTouchSuccessorLifecycle()
        {
            LifecyclePanelCtx ctx = NewLifecyclePanel(false);
            InitializeLifecyclePanel(ctx);
            FakeLifecycleOverlapNonStreamingOrchestrator orchestrator = new();
            Task<string> oldTurn = null;
            Task<string> successorTurn = null;
            try
            {
                ctx.Panel.ChatService = new CoreAiChatService(
                    orchestrator,
                    settings: new StubSettings { EnableStreaming = false });

                oldTurn = ctx.Panel.SubmitMessageFromExternalAsync(
                    "old buffered",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
                while (!orchestrator.FirstStarted.IsCompleted)
                {
                    yield return null;
                }

                int oldTurnGeneration = ctx.Panel.CurrentTurnGeneration;
                ctx.Go.SetActive(false);
                ctx.Go.SetActive(true);
                ctx.ReplaceChatTree();
                ctx.Panel.ChatService = new CoreAiChatService(
                    orchestrator,
                    settings: new StubSettings { EnableStreaming = false });

                int successorGenerationBefore = ctx.Panel.CurrentTurnGeneration;
                successorTurn = ctx.Panel.SubmitMessageFromExternalAsync(
                    "successor buffered",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
                while (!orchestrator.SecondStarted.IsCompleted)
                {
                    yield return null;
                }

                ScrollView successorScroll = CurrentMessageScroll(ctx.Panel);
                Label successorSentinel = new("successor owns this tree");
                successorScroll.Add(successorSentinel);
                yield return DrainAndResetAutoFocusAttempts(ctx.Panel);
                int successorChildCount = successorScroll.childCount;

                orchestrator.ReleaseFirst();
                while (!oldTurn.IsCompleted)
                {
                    yield return null;
                }

                if (oldTurn.IsFaulted)
                {
                    throw oldTurn.Exception.GetBaseException();
                }

                yield return null;
                Assert.IsNull(oldTurn.Result, "the old buffered answer must be discarded after disable");
                Assert.AreEqual(successorGenerationBefore + 1, ctx.Panel.CurrentTurnGeneration);
                Assert.IsTrue(ctx.Panel.IsBusy,
                    "the buffered old finally must not reset the successor turn");
                Assert.AreEqual(successorChildCount, successorScroll.childCount,
                    "the old buffered response must not append an assistant bubble to the new tree");
                Assert.AreSame(successorSentinel, successorScroll[0]);
                Assert.AreEqual(0, ctx.Panel.AutoFocusAttemptCount,
                    "the old buffered turn must not schedule focus after re-enable");

                orchestrator.ReleaseSecond();
                while (!successorTurn.IsCompleted)
                {
                    yield return null;
                }

                if (successorTurn.IsFaulted)
                {
                    throw successorTurn.Exception.GetBaseException();
                }

                Assert.AreEqual("new buffered response", successorTurn.Result);
                Assert.IsFalse(ctx.Panel.IsBusy);
            }
            finally
            {
                orchestrator.ReleaseFirst();
                orchestrator.ReleaseSecond();
                ctx.Dispose();
            }
        }

        /// <summary>
        /// OnDisable publishes busy=false while Unity still considers the component enabled. Lifecycle
        /// ownership must already be inactive so a reentrant subscriber cannot start a second provider call.
        /// </summary>
        [UnityTest]
        public IEnumerator BusyFalseCallback_DuringRealDisable_InactiveSubmitDoesNotReachProvider()
        {
            LifecyclePanelCtx ctx = NewLifecyclePanel(true);
            InitializeLifecyclePanel(ctx);
            ReentrantOwnershipOrchestrator orchestrator = new();
            Task<string> oldTurn = null;
            Task<string> reentrantTurn = null;
            const string expectedInactiveWarning =
                "[CoreAiChatPanel] SubmitMessageFromExternalAsync: ignored (panel inactive).";
            string inactiveWarning = null;
            Application.LogCallback captureWarning = (condition, _, type) =>
            {
                if (type == LogType.Warning && condition.EndsWith(expectedInactiveWarning))
                {
                    inactiveWarning = condition;
                }
            };
            Application.logMessageReceived += captureWarning;
            try
            {
                ctx.Panel.ChatService = new CoreAiChatService(
                    orchestrator,
                    settings: new StubSettings { EnableStreaming = true });
                oldTurn = ctx.Panel.SubmitMessageFromExternalAsync(
                    "old lifecycle",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
                yield return WaitForTask(orchestrator.FirstBubbleRendered, "first lifecycle bubble");

                int falseCallbacks = 0;
                ctx.Panel.BusyStateChanged += busy =>
                {
                    if (busy || reentrantTurn != null)
                    {
                        return;
                    }

                    falseCallbacks++;
                    reentrantTurn = ctx.Panel.SubmitMessageFromExternalAsync(
                        "must stay inactive",
                        new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
                };

                ctx.Go.SetActive(false);

                // WHY: RunAgentTurnAsync deliberately yields once before provider dispatch. Give a broken
                // reentrant turn that bounded opportunity to become observable before checking call count.
                for (int frame = 0; frame < 3 && orchestrator.ProviderCallCount == 1; frame++)
                {
                    yield return null;
                }

                Assert.AreEqual(1, falseCallbacks,
                    "real OnDisable must publish exactly one busy=false transition for the old turn");
                Assert.IsNotNull(reentrantTurn, "the false callback must exercise a reentrant submit");
                Assert.AreEqual(1, orchestrator.ProviderCallCount,
                    "the inactive callback submit must not become a second provider call");
                StringAssert.EndsWith(expectedInactiveWarning, inactiveWarning,
                    "inactive rejection must emit the exact diagnostic without global log suppression");
                orchestrator.ReleaseSecond();
                yield return WaitForTask(reentrantTurn, "inactive reentrant submit");
                yield return WaitForTask(oldTurn, "disabled old turn cleanup");

                Assert.IsNull(reentrantTurn.Result);
                Assert.IsNull(oldTurn.Result);
            }
            finally
            {
                Application.logMessageReceived -= captureWarning;
                orchestrator.ReleaseSecond();
                ctx.Dispose();
            }
        }

        /// <summary>
        /// Busy=true is a public reentrancy boundary. Generation and active CTS must already be installed;
        /// a synchronous StopAgent from the callback must prevent any provider invocation.
        /// </summary>
        [UnityTest]
        public IEnumerator BusyTrueCallback_StopAgent_PreventsProviderCallAndReleasesActiveCts()
        {
            LifecyclePanelCtx ctx = NewLifecyclePanel(false);
            InitializeLifecyclePanel(ctx);
            ReentrantOwnershipOrchestrator orchestrator = new();
            try
            {
                ctx.Panel.ChatService = new CoreAiChatService(
                    orchestrator,
                    settings: new StubSettings { EnableStreaming = false });

                int observedGeneration = 0;
                CancellationTokenSource observedCts = null;
                bool observedCtsCancelled = false;
                bool stopInvoked = false;
                ctx.Panel.BusyStateChanged += busy =>
                {
                    if (!busy || stopInvoked)
                    {
                        return;
                    }

                    stopInvoked = true;
                    observedGeneration = ctx.Panel.CurrentTurnGeneration;
                    observedCts = GetField<CancellationTokenSource>(ctx.Panel, "_activeRequestCts");
                    ctx.Panel.StopAgent();
                    observedCtsCancelled = observedCts != null && observedCts.IsCancellationRequested;
                };

                Task<string> turn = ctx.Panel.SubmitMessageFromExternalAsync(
                    "stop before provider",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
                yield return WaitForTask(turn, "busy=true synchronous stop");

                Assert.IsTrue(stopInvoked);
                Assert.Greater(observedGeneration, 0,
                    "busy=true must observe the generation already assigned to the turn");
                Assert.IsNotNull(observedCts,
                    "busy=true must observe the request CTS before a handler can call StopAgent");
                Assert.IsTrue(observedCtsCancelled);
                Assert.AreEqual(0, orchestrator.ProviderCallCount,
                    "StopAgent inside busy=true must prevent entry into the provider");
                Assert.IsNull(turn.Result);
                Assert.IsNull(GetField<CancellationTokenSource>(ctx.Panel, "_activeRequestCts"));
                Assert.IsFalse(ctx.Panel.IsBusy);
            }
            finally
            {
                ctx.Dispose();
            }
        }

        /// <summary>
        /// StopAgent's busy=false callback may immediately start the next turn. No tail from the stopped
        /// turn may clear the successor's busy flags, CTS, or active streaming bubble.
        /// </summary>
        [UnityTest]
        public IEnumerator StopAgent_BusyFalseCallback_StartsSuccessorWithoutOldTailClobber()
        {
            LifecyclePanelCtx ctx = NewLifecyclePanel(true);
            InitializeLifecyclePanel(ctx);
            ReentrantOwnershipOrchestrator orchestrator = new();
            Task<string> oldTurn = null;
            Task<string> successorTurn = null;
            try
            {
                ctx.Panel.ChatService = new CoreAiChatService(
                    orchestrator,
                    settings: new StubSettings { EnableStreaming = true });
                oldTurn = ctx.Panel.SubmitMessageFromExternalAsync(
                    "turn to stop",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
                yield return WaitForTask(orchestrator.FirstBubbleRendered, "old streaming bubble");

                int falseCallbacks = 0;
                ctx.Panel.BusyStateChanged += busy =>
                {
                    if (busy || successorTurn != null)
                    {
                        return;
                    }

                    falseCallbacks++;
                    successorTurn = ctx.Panel.SubmitMessageFromExternalAsync(
                        "successor from callback",
                        new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
                };

                ctx.Panel.StopAgent();
                Assert.AreEqual(1, falseCallbacks);
                Assert.IsNotNull(successorTurn,
                    "StopAgent busy=false must synchronously hand the panel to the successor");
                Assert.IsTrue(ctx.Panel.IsBusy,
                    "StopAgent must return with the reentrant successor's busy ownership intact");
                Assert.IsNotNull(GetField<CancellationTokenSource>(ctx.Panel, "_activeRequestCts"),
                    "StopAgent must return with the reentrant successor's CTS installed");
                yield return WaitForTask(orchestrator.SecondBubbleRendered, "successor streaming bubble");

                ScrollView successorScroll = CurrentMessageScroll(ctx.Panel);
                Label activeBubble = successorScroll
                    .Query<Label>(className: CoreAiChatPanel.StreamingActiveUssClassName)
                    .First();
                Assert.IsTrue(ctx.Panel.IsBusy,
                    "the old StopAgent tail must not reset the successor busy state");
                Assert.IsNotNull(GetField<CancellationTokenSource>(ctx.Panel, "_activeRequestCts"));
                Assert.AreEqual("successor response", activeBubble.text);
                Assert.IsTrue(activeBubble.ClassListContains(CoreAiChatPanel.StreamingActiveUssClassName));
                Assert.AreEqual(2, orchestrator.ProviderCallCount);

                orchestrator.ReleaseSecond();
                yield return WaitForTask(successorTurn, "successor completion");
                yield return WaitForTask(oldTurn, "stopped old turn cleanup");

                Assert.AreEqual("successor response", successorTurn.Result);
                Assert.IsNull(oldTurn.Result);
                Assert.IsFalse(ctx.Panel.IsBusy);
                Assert.IsNull(GetField<CancellationTokenSource>(ctx.Panel, "_activeRequestCts"));
            }
            finally
            {
                orchestrator.ReleaseSecond();
                ctx.Dispose();
            }
        }

        private const string LifecycleFocusOwnerName = "coreai-lifecycle-focus-owner";
        private const float ReentrantTestTimeoutSeconds = 8f;

        private static IEnumerator WaitForTask(Task task, string operation)
        {
            Assert.IsNotNull(task, $"{operation}: task was not created");
            float deadline = Time.realtimeSinceStartup + ReentrantTestTimeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsTrue(task.IsCompleted,
                $"{operation}: timed out after {ReentrantTestTimeoutSeconds:F0}s");
            if (task.IsFaulted)
            {
                throw task.Exception.GetBaseException();
            }

            Assert.IsFalse(task.IsCanceled, $"{operation}: task escaped as canceled");
        }

        private static IEnumerator DrainAndResetAutoFocusAttempts(LifecycleTestPanel panel)
        {
            // WHY: rebind and active-successor setup legitimately enqueue their own autofocus callbacks.
            // Drain those callbacks before measuring whether the stale lifecycle enqueues another one.
            yield return null;
            yield return null;
            panel.ResetAutoFocusAttemptCount();
        }

        private static int CountActiveStreamingBubbles(VisualElement root)
        {
            return root.Query<VisualElement>(className: CoreAiChatPanel.StreamingActiveUssClassName)
                .ToList()
                .Count;
        }

        private static ScrollView CurrentMessageScroll(CoreAiChatPanel panel)
        {
            return GetField<ScrollView>(panel, "MessageScroll");
        }

        private static LifecyclePanelCtx NewLifecyclePanel(bool enableStreaming)
        {
            PanelSettings panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            GameObject go = new("CoreAiChatPanel_Lifecycle_Test");
            go.SetActive(false);
            UIDocument document = go.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            LifecycleTestPanel panel = go.AddComponent<LifecycleTestPanel>();
            panel.SetRuntimeOptions(new CoreAiChatOptions
            {
                RoleId = "SmartChat",
                WelcomeMessage = string.Empty,
                LoadPersistedChatOnStartup = false,
                EnableStreaming = enableStreaming,
                EnableCameraTool = false
            });
            go.SetActive(true);

            return new LifecyclePanelCtx(go, panelSettings, document, panel);
        }

        private static void InitializeLifecyclePanel(LifecyclePanelCtx ctx)
        {
            Assert.IsNotNull(ctx.Document.rootVisualElement,
                "UIDocument root must exist synchronously in the PlayMode fixture");
            ctx.Document.rootVisualElement.Add(BuildLifecycleChatTree());

            // WHY: drive real OnDisable/OnEnable callbacks after attaching the synthetic tree.
            ctx.Panel.enabled = false;
            ctx.Panel.enabled = true;
            Assert.IsNotNull(CurrentMessageScroll(ctx.Panel),
                "CoreAiChatPanel did not bind the synthetic message scroll");
        }

        private static VisualElement BuildLifecycleChatTree()
        {
            VisualElement root = new() { name = "coreai-lifecycle-root" };
            VisualElement container = new() { name = "coreai-chat-root" };
            container.Add(new ScrollView { name = "coreai-chat-scroll" });
            container.Add(new TextField { name = "coreai-chat-input" });
            container.Add(new Button { name = "coreai-chat-send" });
            container.Add(new Button { name = "coreai-chat-clear" });
            container.Add(new Button { name = "coreai-chat-collapse" });

            VisualElement typing = new() { name = "coreai-typing-indicator" };
            typing.Add(new VisualElement { name = "coreai-typing-avatar" });
            typing.Add(new Label { name = "coreai-typing-label" });
            container.Add(typing);
            root.Add(container);

            Button fab = new() { name = "coreai-chat-fab" };
            fab.style.display = DisplayStyle.None;
            root.Add(fab);
            root.Add(new Button { name = LifecycleFocusOwnerName, focusable = true });
            return root;
        }

        private sealed class LifecyclePanelCtx : System.IDisposable
        {
            private readonly PanelSettings _panelSettings;

            public LifecyclePanelCtx(
                GameObject go,
                PanelSettings panelSettings,
                UIDocument document,
                LifecycleTestPanel panel)
            {
                Go = go;
                _panelSettings = panelSettings;
                Document = document;
                Panel = panel;
            }

            public GameObject Go { get; }

            public UIDocument Document { get; }

            public LifecycleTestPanel Panel { get; }

            public void ReplaceChatTree()
            {
                Document.rootVisualElement.Clear();
                Document.rootVisualElement.Add(BuildLifecycleChatTree());

                // WHY: the UIDocument stays active while its tree is replaced, so rebind the production
                // component through real lifecycle callbacks to the replacement elements.
                Panel.enabled = false;
                Panel.enabled = true;
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Go);
                Object.DestroyImmediate(_panelSettings);
            }
        }

        /// <summary>
        /// Lifecycle overlap tests own focus explicitly. Production autofocus is valid for an active
        /// successor turn, but its scheduled callback would race the fixture's sentinel focus and obscure
        /// whether the stale lifecycle itself touched the replacement tree.
        /// </summary>
        private sealed class LifecycleTestPanel : CoreAiChatPanel
        {
            public int AutoFocusAttemptCount { get; private set; }

            protected override bool AutoFocusInputFieldEnabled
            {
                get
                {
                    AutoFocusAttemptCount++;
                    return false;
                }
            }

            public void ResetAutoFocusAttemptCount()
            {
                AutoFocusAttemptCount = 0;
            }
        }

        private static void SetFlag(CoreAiChatPanel panel, string fieldName, bool value)
        {
            typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, value);
        }

        private static T GetField<T>(CoreAiChatPanel panel, string fieldName)
        {
            return (T)typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(panel);
        }

        private static void InvokeUpdateSendButton(CoreAiChatPanel panel)
        {
            // WHY: UpdateSendButtonVisualState() is the funnel that fires BusyStateChanged on transitions.
            // It's called automatically by every flag mutation in production code; the test reaches
            // it through reflection because we mutated flags directly via SetFlag.
            typeof(CoreAiChatPanel)
                .GetMethod("UpdateSendButtonVisualState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, null);
        }

        private static void SetField(CoreAiChatPanel panel, string fieldName, object? value)
        {
            typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, value);
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
            public float? LlmRequestTimeoutSecondsOverride { get; set; }
            public float LlmRequestTimeoutSeconds => LlmRequestTimeoutSecondsOverride ?? 15f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming { get; set; } = true;
        }

        /// <summary>
        /// Streams one visible chunk, then parks until <see cref="Release"/> is called. Lets a test act on
        /// the panel at the exact moment a turn has an OPEN streaming bubble.
        /// </summary>
        private sealed class FakeGatedStreamOrchestrator : IAiOrchestrationService
        {
            private readonly TaskCompletionSource<bool> _bubbleRendered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Completes once the panel has consumed the first text chunk (bubble is on screen).</summary>
            public Task BubbleRendered => _bubbleRendered.Task;

            /// <summary>Lets the parked turn run to completion.</summary>
            public void Release()
            {
                _release.TrySetResult(true);
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(string.Empty);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                yield return new LlmStreamChunk { Text = "часть ответа" };

                // WHY: reached only when the panel asks for the NEXT chunk, after it rendered the bubble.
                _bubbleRendered.TrySetResult(true);
                await _release.Task;
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class FakeLifecycleOverlapStreamOrchestrator : IAiOrchestrationService
        {
            private readonly TaskCompletionSource<bool> _firstBubbleRendered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _secondBubbleRendered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _releaseFirst =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _releaseSecond =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _streamCalls;

            public Task FirstBubbleRendered => _firstBubbleRendered.Task;

            public Task SecondBubbleRendered => _secondBubbleRendered.Task;

            public void ReleaseFirstWithFailure()
            {
                _releaseFirst.TrySetResult(true);
            }

            public void ReleaseSecond()
            {
                _releaseSecond.TrySetResult(true);
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(string.Empty);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                int call = Interlocked.Increment(ref _streamCalls);
                if (call == 1)
                {
                    yield return new LlmStreamChunk { Text = "old response" };
                    _firstBubbleRendered.TrySetResult(true);
                    await _releaseFirst.Task;
                    throw new System.InvalidOperationException("Simulated late lifecycle stream failure.");
                }

                yield return new LlmStreamChunk { Text = "successor response" };
                _secondBubbleRendered.TrySetResult(true);
                await _releaseSecond.Task;
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class FakeLifecycleOverlapNonStreamingOrchestrator : IAiOrchestrationService
        {
            private readonly TaskCompletionSource<bool> _firstStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _secondStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _releaseFirst =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _releaseSecond =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _taskCalls;

            public Task FirstStarted => _firstStarted.Task;

            public Task SecondStarted => _secondStarted.Task;

            public void ReleaseFirst()
            {
                _releaseFirst.TrySetResult(true);
            }

            public void ReleaseSecond()
            {
                _releaseSecond.TrySetResult(true);
            }

            public async Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                int call = Interlocked.Increment(ref _taskCalls);
                if (call == 1)
                {
                    _firstStarted.TrySetResult(true);
                    await _releaseFirst.Task;
                    return "old buffered response";
                }

                _secondStarted.TrySetResult(true);
                await _releaseSecond.Task;
                return "new buffered response";
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                await Task.Yield();
                yield break;
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class ReentrantOwnershipOrchestrator : IAiOrchestrationService
        {
            private readonly TaskCompletionSource<bool> _firstBubbleRendered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _secondBubbleRendered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _releaseSecond =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _providerCalls;
            private int _streamCalls;

            public int ProviderCallCount => _providerCalls;

            public Task FirstBubbleRendered => _firstBubbleRendered.Task;

            public Task SecondBubbleRendered => _secondBubbleRendered.Task;

            public void ReleaseSecond()
            {
                _releaseSecond.TrySetResult(true);
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                Interlocked.Increment(ref _providerCalls);
                return Task.FromResult("buffered provider response");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                Interlocked.Increment(ref _providerCalls);
                int call = Interlocked.Increment(ref _streamCalls);
                if (call == 1)
                {
                    yield return new LlmStreamChunk { Text = "old response" };
                    _firstBubbleRendered.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, ct);
                    yield break;
                }

                yield return new LlmStreamChunk { Text = "successor response" };
                _secondBubbleRendered.TrySetResult(true);
                await _releaseSecond.Task;
                ct.ThrowIfCancellationRequested();
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }
    }
}
