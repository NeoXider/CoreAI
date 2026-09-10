using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="CoreAiChatService"/> streaming selection,
    /// smart-send behavior, fake-orchestrator send paths, and persisted chat formatting.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatServiceEditModeTests
    {
        /// <summary>
        /// Upper bound on real time a virtual-clock test waits for an async hop to land; only a hang guard,
        /// never a timing assertion.
        /// </summary>
        private const float HangGuardSeconds = 15f;

        [SetUp]
        public void SetUp()
        {
            CoreAISettings.ResetOverrides();
            CoreAISettings.Instance = null;
            CoreAiChatService.IdleTimeoutDeadline.SchedulerOverride = null;
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettings.ResetOverrides();
            CoreAISettings.Instance = null;
            CoreAiChatService.IdleTimeoutDeadline.SchedulerOverride = null;
        }

        // ===================== Persisted chat (session restore for UI) =====================

        [Test]
        public void TryGetPersistedChatHistory_NoStore_ReturnsFalse()
        {
            CoreAiChatService service = new(new FakeAiOrchestrator("ok"), memoryStore: null);

            bool ok = service.TryGetPersistedChatHistory("SmartChat", out ChatMessage[] msgs, 0);

            Assert.IsFalse(ok);
            Assert.IsNotNull(msgs);
            Assert.AreEqual(0, msgs.Length);
        }

        [Test]
        public void TryGetPersistedChatHistory_EmptyHistory_ReturnsFalse()
        {
            ListBackedChatHistoryStore store = new();
            CoreAiChatService service = new(new FakeAiOrchestrator("ok"), memoryStore: store);

            Assert.IsFalse(service.TryGetPersistedChatHistory("SmartChat", out ChatMessage[] msgs, 0));
        }

        [Test]
        public void TryGetPersistedChatHistory_ReturnsTailWhenMaxMessagesSet()
        {
            ListBackedChatHistoryStore store = new();
            const string role = "SmartChat";
            for (int i = 0; i < 5; i++)
            {
                store.AppendChatMessage(role, i % 2 == 0 ? "user" : "assistant", $"m{i}", false);
            }

            CoreAiChatService service = new(new FakeAiOrchestrator("ok"), memoryStore: store);

            Assert.IsTrue(service.TryGetPersistedChatHistory(role, out ChatMessage[] msgs, 2));
            Assert.AreEqual(2, msgs.Length);
            Assert.AreEqual("m3", msgs[0].Content);
            Assert.AreEqual("m4", msgs[1].Content);
        }

        [Test]
        public void PersistedChat_UiFormattingRoundTrip_MatchesCoreAiChatPanelRules()
        {
            ListBackedChatHistoryStore store = new();
            const string role = "SmartChat";
            string userComposer =
                "{\"telemetry\":{},\"hint\":\"stored user line\",\"ai_task_source\":\"Chat\"}";
            store.AppendChatMessage(role, "user", userComposer, false);
            store.AppendChatMessage(role, "assistant", "visible reply", false);

            CoreAiChatService service = new(new FakeAiOrchestrator("ok"), memoryStore: store);
            Assert.IsTrue(service.TryGetPersistedChatHistory(role, out ChatMessage[] msgs, 0));
            Assert.AreEqual(2, msgs.Length);

            string userLine = CoreAiChatPanel.FormatPersistedMessageForUi(msgs[0].Content, true);
            string assistantLine = CoreAiChatPanel.FormatPersistedMessageForUi(msgs[1].Content, false);

            Assert.AreEqual("stored user line", userLine);
            Assert.AreEqual("visible reply", assistantLine);
        }

        // ===================== IsStreamingEnabled — fallbacks =====================

        [Test]
        public void IsStreamingEnabled_NoPolicyNoSettings_FallsBackToStaticDefault()
        {
            CoreAiChatService service = new(new FakeAiOrchestrator("ok"));

            // Default CoreAISettings.EnableStreaming = true
            Assert.IsTrue(service.IsStreamingEnabled("AnyRole", uiOverride: true));

            CoreAISettings.EnableStreaming = false;
            Assert.IsFalse(service.IsStreamingEnabled("AnyRole", uiOverride: true));
        }

        [Test]
        public void IsStreamingEnabled_WithSettingsOnly_UsesSettingsFlag()
        {
            StubSettings settings = new() { EnableStreaming = false };
            CoreAiChatService service = new(new FakeAiOrchestrator("ok"),
                null,
                settings);

            Assert.IsFalse(service.IsStreamingEnabled("AnyRole", uiOverride: true));

            settings.EnableStreaming = true;
            Assert.IsTrue(service.IsStreamingEnabled("AnyRole", uiOverride: true));
        }

        [Test]
        public void IsStreamingEnabled_PerRoleOverride_WinsOverSettings()
        {
            StubSettings settings = new() { EnableStreaming = false };
            AgentMemoryPolicy policy = new();
            policy.SetStreamingEnabled("FastRole", true);

            CoreAiChatService service = new(new FakeAiOrchestrator("ok"),
                policy,
                settings);

            Assert.IsTrue(service.IsStreamingEnabled("FastRole", uiOverride: true), "per-role override wins");
            Assert.IsFalse(service.IsStreamingEnabled("OtherRole", uiOverride: true), "other roles → global");
        }

        // ===================== IsStreamingEnabled — UI layer =====================

        [Test]
        public void IsStreamingEnabled_UiFallbackFalse_ForcesOff()
        {
            StubSettings settings = new() { EnableStreaming = true };
            AgentMemoryPolicy policy = new();
            policy.SetStreamingEnabled("Role", true);

            CoreAiChatService service = new(new FakeAiOrchestrator("ok"),
                policy,
                settings);

            // The UI layer turned streaming off, so nothing else gets a say.
            Assert.IsFalse(service.IsStreamingEnabled("Role", uiOverride: false));
        }

        [Test]
        public void IsStreamingEnabled_UiOverrideFalse_ForcesOff()
        {
            StubSettings settings = new() { EnableStreaming = true };
            CoreAiChatService service = new(new FakeAiOrchestrator("ok"),
                null,
                settings);

            // The bool? overload: false forces off; true and null both mean "decide as usual".
            Assert.IsFalse(service.IsStreamingEnabled("Role", (bool?)false));
            Assert.IsTrue(service.IsStreamingEnabled("Role", (bool?)true));
            Assert.IsTrue(service.IsStreamingEnabled("Role", (bool?)null));
        }

        // ===================== SendMessage — happy path =====================

        [Test]
        public async Task SendMessageAsync_NonStreaming_ReturnsContent()
        {
            FakeAiOrchestrator orchestrator = new("Hello, world!");
            CoreAiChatService service = new(orchestrator);

            string response = await service.SendMessageAsync("hi", "TestRole");

            Assert.AreEqual("Hello, world!", response);
            Assert.AreEqual(1, orchestrator.CompleteCallCount);
            Assert.AreEqual(0, orchestrator.StreamingCallCount);
        }

        [Test]
        public async Task SendMessageAsync_Error_PropagatesException()
        {
            // v1.5.1: CoreAiChatService no longer swallows exceptions.
            // Errors propagate to the caller (CoreAiChatPanel), which displays them.
            FakeAiOrchestrator orchestrator = new(null, "connection refused");
            CoreAiChatService service = new(orchestrator);

            Exception ex = null;
            try
            {
                await service.SendMessageAsync("hi", "TestRole");
            }
            catch (Exception e)
            {
                ex = e;
            }

            Assert.NotNull(ex, "expected Exception");
            Assert.AreEqual("connection refused", ex.Message);
        }

        [Test]
        public async Task SendMessageStreamingAsync_YieldsChunks_InOrder()
        {
            FakeAiOrchestrator orchestrator = new(streamChunks: new[] { "Hel", "lo", " world" });
            CoreAiChatService service = new(orchestrator);

            List<string> visible = new();
            await foreach (LlmStreamChunk chunk in
                           service.SendMessageStreamingAsync("hi", "TestRole"))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    visible.Add(chunk.Text);
                }
            }

            CollectionAssert.AreEqual(new[] { "Hel", "lo", " world" }, visible);
            Assert.AreEqual(1, orchestrator.StreamingCallCount);
        }

        // ===================== SendMessageSmartAsync — auto selection =====================

        [Test]
        public async Task SendSmart_StreamingEnabled_UsesStreamingPath()
        {
            FakeAiOrchestrator orchestrator = new(streamChunks: new[] { "A", "B", "C" });
            StubSettings settings = new() { EnableStreaming = true };
            CoreAiChatService service = new(orchestrator,
                null,
                settings);

            List<string> chunks = new();
            string full = await service.SendMessageSmartAsync(
                "hi", "Role",
                c =>
                {
                    if (!string.IsNullOrEmpty(c.Text))
                    {
                        chunks.Add(c.Text);
                    }
                });

            Assert.AreEqual("ABC", full);
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, chunks);
            Assert.AreEqual(1, orchestrator.StreamingCallCount);
            Assert.AreEqual(0, orchestrator.CompleteCallCount);
        }

        [Test]
        public async Task SendSmart_StreamingDisabled_UsesNonStreamingPath()
        {
            FakeAiOrchestrator orchestrator = new("Full response text");
            StubSettings settings = new() { EnableStreaming = false };
            CoreAiChatService service = new(orchestrator,
                null,
                settings);

            List<string> chunks = new();
            string full = await service.SendMessageSmartAsync(
                "hi", "Role",
                c =>
                {
                    if (!string.IsNullOrEmpty(c.Text))
                    {
                        chunks.Add(c.Text);
                    }
                });

            Assert.AreEqual("Full response text", full);
            Assert.AreEqual(1, orchestrator.CompleteCallCount);
            Assert.AreEqual(0, orchestrator.StreamingCallCount);

            // onChunk fires on the non-streaming path too: one chunk carrying the whole text, then the final.
            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual("Full response text", chunks[0]);
        }

        [Test]
        public async Task SendSmart_UiOverrideFalse_ForcesNonStreaming()
        {
            FakeAiOrchestrator orchestrator = new("Non-streaming answer");
            StubSettings settings = new() { EnableStreaming = true };
            CoreAiChatService service = new(orchestrator,
                null,
                settings);

            string full = await service.SendMessageSmartAsync(
                "hi", "Role",
                null,
                false);

            Assert.AreEqual("Non-streaming answer", full);
            Assert.AreEqual(1, orchestrator.CompleteCallCount);
            Assert.AreEqual(0, orchestrator.StreamingCallCount);
        }

        // ===================== Control API =====================

        [Test]
        public void ClearHistory_ClearsMemoryStore()
        {
            FakeMemoryStore store = new();
            CoreAiChatService service = new(new FakeAiOrchestrator("ok"), memoryStore: store);

            service.ClearHistory("Role123");

            Assert.AreEqual("Role123", store.ClearedRole);
        }

        [Test]
        public void StopAgent_CallsFacade_DoesNotThrowWithoutScope()
        {
            CoreAiChatService service = new(new FakeAiOrchestrator("ok"));

            // EditMode has no CoreAILifetimeScope, so StopAgent has to degrade quietly instead of throwing.
            Assert.DoesNotThrow(() => service.StopAgent("Role"));
        }

        // ===================== v1.5.1 — Timeout + Error Propagation =====================

        [Test]
        public async Task SendMessageAsync_WithTimeoutSettings_PassesCancellationToken()
        {
            // v1.5.1: timeout is now enforced by CoreAiChatService via UniTask CancelAfterSlim.
            // Verify that when LlmRequestTimeoutSeconds > 0, the orchestrator receives
            // a different CancellationToken (linked with timeout) than the caller's original.
            TokenCapturingOrchestrator orchestrator = new();
            StubSettings settings = new() { LlmRequestTimeoutSecondsOverride = 30f };
            CoreAiChatService service = new(orchestrator, settings: settings);

            await service.SendMessageAsync("hi", "TestRole");

            // The service should have created a linked CTS with CancelAfterSlim
            Assert.IsTrue(orchestrator.LastCancellationToken.CanBeCanceled,
                "When timeout > 0, orchestrator should receive a cancellable token");
        }

        [Test]
        public async Task SendMessageAsync_NoTimeoutSettings_PassesOriginalToken()
        {
            // When LlmRequestTimeoutSeconds = 0, no timeout CTS is created
            TokenCapturingOrchestrator orchestrator = new();
            CoreAiChatService service = new(orchestrator); // no settings = no timeout

            using CancellationTokenSource cts = new();
            await service.SendMessageAsync(
                new AiTaskRequest { RoleId = "Role", Hint = "hi" }, cts.Token);

            // Should pass the caller's token directly (not a linked one)
            Assert.AreEqual(cts.Token, orchestrator.LastCancellationToken,
                "Without timeout settings, original token should pass through");
        }

        [Test]
        public async Task SendMessageAsync_NullResult_ReturnsEmptyString()
        {
            // AiOrchestrator may return null on soft failures;
            // CoreAiChatService should convert to "" (not crash)
            FakeAiOrchestrator orchestrator = new(null);
            CoreAiChatService service = new(orchestrator);

            string response = await service.SendMessageAsync("hi", "TestRole");
            Assert.AreEqual("", response, "null result from orchestrator → empty string");
        }

        /// <summary>
        /// <see cref="CoreAiChatService"/> uses UniTask <c>CancelAfterSlim</c> (player loop). A plain
        /// <see cref="Test"/> that blocks the main thread on <c>Task.Delay(Infinite, ct)</c> can deadlock
        /// because the timer never runs — use <see cref="UnityTest"/> and yield frames.
        /// </summary>
        [UnityTest]
        [Timeout(20000)]
        public IEnumerator SendMessageAsync_TimeoutWhenOrchestratorBlocks_ThrowsLlmOperationTimeoutException()
        {
            float previousTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            try
            {
                BlockUntilCancelledOrchestrator orchestrator = new();
                StubSettings settings = new() { LlmRequestTimeoutSecondsOverride = 0.2f };
                CoreAiChatService service = new(orchestrator, settings: settings);

                Task task = service.SendMessageAsync("hi", "TestRole");

                float deadline = Time.realtimeSinceStartup + 15f;
                while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.IsTrue(task.IsCompleted,
                    "SendMessageAsync should complete in real time while the game is paused.");
                try
                {
                    task.GetAwaiter().GetResult();
                    Assert.Fail("Expected LlmOperationTimeoutException after timeout.");
                }
                catch (LlmOperationTimeoutException ex)
                {
                    Assert.That(ex.Message, Does.Contain("timed out"));
                }
                catch (Exception ex)
                {
                    Assert.Fail($"Expected LlmOperationTimeoutException, got {ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                Time.timeScale = previousTimeScale;
            }
        }

        // ===================== Idle/no-progress timeout (whole-turn budget fix) =====================

        /// <summary>
        /// Regression: the timeout used to cover the whole turn, so several quick chunks whose durations
        /// add up past <c>LlmRequestTimeoutSeconds</c> would cancel a perfectly healthy stream. Each yielded
        /// chunk must re-arm the deadline, so only a real stall (no chunk for the full window) should time out.
        /// </summary>
        [UnityTest]
        [Timeout(20000)]
        public IEnumerator SendMessageStreamingAsync_SteadyChunksExceedingTotalWindow_DoesNotTimeOut()
        {
            // WHY a virtual clock: the idle window is measured on the clock the test advances, not on wall
            // time. The earlier wall-clock version (6 x 80 ms against 300 ms) looked like a 3.75x margin but
            // really ran at 200-275 ms per chunk in EditMode — every chunk crosses the Unity sync context
            // three times and each hop waits for an editor tick — and it timed out under load. Do not
            // replace the gates with Task.Delay again.
            string[] chunks = { "a", "b", "c", "d", "e", "f" };
            TimeSpan window = TimeSpan.FromMilliseconds(300);
            TimeSpan gap = TimeSpan.FromMilliseconds(80);
            Assert.Less(gap, window, "shape: no single gap may reach the idle window");
            Assert.Greater(TimeSpan.FromTicks(gap.Ticks * chunks.Length), window,
                "shape: the gaps must add up past the idle window, or the whole-turn regression is invisible");

            VirtualIdleClock clock = new();
            CoreAiChatService.IdleTimeoutDeadline.SchedulerOverride = clock.Schedule;
            try
            {
                GatedChunkOrchestrator orchestrator = new(chunks);
                StubSettings settings = new() { LlmRequestTimeoutSecondsOverride = (float)window.TotalSeconds };
                CoreAiChatService service = new(orchestrator, settings: settings);

                List<string> received = new();
                Exception failure = null;

                async Task DriveAsync()
                {
                    try
                    {
                        await foreach (LlmStreamChunk chunk in service.SendMessageStreamingAsync("hi", "TestRole"))
                        {
                            if (!string.IsNullOrEmpty(chunk.Text))
                            {
                                received.Add(chunk.Text);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                }

                Task drive = DriveAsync();

                for (int i = 0; i < chunks.Length; i++)
                {
                    orchestrator.Release(i);
                    float guard = Time.realtimeSinceStartup + HangGuardSeconds;
                    while (received.Count <= i && !drive.IsCompleted && Time.realtimeSinceStartup < guard)
                    {
                        yield return null;
                    }

                    Assert.AreEqual(i + 1, received.Count, $"chunk {i} never arrived; failure: {failure}");
                    Assert.AreEqual(clock.Now + window, clock.PendingDeadline,
                        $"chunk {i} must re-arm the idle deadline to one full window from now");
                    clock.Advance(gap);
                }

                orchestrator.Release(chunks.Length);
                float finalGuard = Time.realtimeSinceStartup + HangGuardSeconds;
                while (!drive.IsCompleted && Time.realtimeSinceStartup < finalGuard)
                {
                    yield return null;
                }

                Assert.IsTrue(drive.IsCompleted, "streaming never finished after the last chunk was released");
                Assert.IsNull(failure, $"expected no timeout, got: {failure}");
                CollectionAssert.AreEqual(chunks, received);
                Assert.Greater(clock.Now, window, "the turn as a whole must have outlived the idle window");
                Assert.IsNull(clock.PendingDeadline, "a finished turn must leave no deadline armed");
            }
            finally
            {
                CoreAiChatService.IdleTimeoutDeadline.SchedulerOverride = null;
            }
        }

        /// <summary>
        /// A real stall (no chunk for the full idle window) must still time out — the per-chunk rearm must
        /// not mask a genuinely stuck stream.
        /// </summary>
        [UnityTest]
        [Timeout(20000)]
        public IEnumerator SendMessageStreamingAsync_StallsAfterFirstChunk_ThrowsLlmOperationTimeoutException()
        {
            float previousTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            try
            {
                StallAfterFirstChunkOrchestrator orchestrator = new();
                StubSettings settings = new() { LlmRequestTimeoutSecondsOverride = 0.2f };
                CoreAiChatService service = new(orchestrator, settings: settings);

                Exception failure = null;

                async Task DriveAsync()
                {
                    try
                    {
                        await foreach (LlmStreamChunk _ in service.SendMessageStreamingAsync("hi", "TestRole"))
                        {
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                }

                Task drive = DriveAsync();

                float deadline = Time.realtimeSinceStartup + 15f;
                while (!drive.IsCompleted && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.IsTrue(drive.IsCompleted, "streaming should finish in real time while the game is paused.");
                Assert.IsInstanceOf<LlmOperationTimeoutException>(failure);
            }
            finally
            {
                Time.timeScale = previousTimeScale;
            }
        }

        /// <summary>
        /// Regression (non-streaming path): a multi-tool-call turn whose steps add up past
        /// <c>LlmRequestTimeoutSeconds</c> must not be cancelled — <see cref="CoreAi.OnToolCallStarted"/> and
        /// <see cref="CoreAi.OnToolCallCompleted"/> for the request's RoleId must re-arm the deadline.
        /// </summary>
        [UnityTest]
        [Timeout(20000)]
        public IEnumerator SendMessageAsync_ToolCallProgressExceedingTotalWindow_DoesNotTimeOut()
        {
            // WHY a virtual clock: same hazard as the streaming test above — the wall-clock version
            // (4 x 100 ms against 250 ms) left about one editor tick of slack per step.
            const int steps = 4;
            TimeSpan window = TimeSpan.FromMilliseconds(250);
            TimeSpan stepDuration = TimeSpan.FromMilliseconds(100);
            Assert.Less(stepDuration, window, "shape: no single step may reach the idle window");
            Assert.Greater(TimeSpan.FromTicks(stepDuration.Ticks * steps), window,
                "shape: the steps must add up past the idle window, or the whole-turn regression is invisible");

            VirtualIdleClock clock = new();
            CoreAiChatService.IdleTimeoutDeadline.SchedulerOverride = clock.Schedule;
            try
            {
                GatedToolCallOrchestrator orchestrator = new(steps);
                StubSettings settings = new() { LlmRequestTimeoutSecondsOverride = (float)window.TotalSeconds };
                CoreAiChatService service = new(orchestrator, settings: settings);

                string result = null;
                Exception failure = null;

                async Task DriveAsync()
                {
                    try
                    {
                        result = await service.SendMessageAsync("hi", "TestRole");
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                }

                Task drive = DriveAsync();

                for (int i = 0; i < steps; i++)
                {
                    float guard = Time.realtimeSinceStartup + HangGuardSeconds;
                    while (orchestrator.StartedSteps <= i && !drive.IsCompleted && Time.realtimeSinceStartup < guard)
                    {
                        yield return null;
                    }

                    Assert.AreEqual(i + 1, orchestrator.StartedSteps, $"tool call {i} never started; failure: {failure}");
                    Assert.AreEqual(clock.Now + window, clock.PendingDeadline,
                        $"tool call {i} starting must re-arm the idle deadline to one full window from now");
                    clock.Advance(stepDuration);
                    orchestrator.Release(i);
                }

                float finalGuard = Time.realtimeSinceStartup + HangGuardSeconds;
                while (!drive.IsCompleted && Time.realtimeSinceStartup < finalGuard)
                {
                    yield return null;
                }

                Assert.IsTrue(drive.IsCompleted, "the turn never finished after the last tool call was released");
                Assert.IsNull(failure, $"expected no timeout, got: {failure}");
                Assert.AreEqual("done", result);
                Assert.Greater(clock.Now, window, "the turn as a whole must have outlived the idle window");
                Assert.IsNull(clock.PendingDeadline, "a finished turn must leave no deadline armed");
            }
            finally
            {
                CoreAiChatService.IdleTimeoutDeadline.SchedulerOverride = null;
            }
        }

        // ===================== Helpers =====================

        private sealed class StubSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix { get; set; } = "";
            public float Temperature { get; set; } = 0.1f;
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
        /// Minimal in-memory <see cref="IAgentMemoryStore"/> for chat history only (mirrors tail semantics of <c>FileAgentMemoryStore.GetChatHistory</c>).
        /// </summary>
        private sealed class ListBackedChatHistoryStore : IAgentMemoryStore
        {
            private readonly Dictionary<string, List<ChatMessage>> _history = new();

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
            }

            public void Clear(string roleId)
            {
                _history.Remove(roleId);
            }

            public void ClearChatHistory(string roleId)
            {
                _history.Remove(roleId);
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
                if (!_history.TryGetValue(roleId, out List<ChatMessage> list))
                {
                    list = new List<ChatMessage>();
                    _history[roleId] = list;
                }

                list.Add(new ChatMessage(role, content));
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                if (!_history.TryGetValue(roleId, out List<ChatMessage> list) || list.Count == 0)
                {
                    return Array.Empty<ChatMessage>();
                }

                if (maxMessages > 0 && list.Count > maxMessages)
                {
                    return list.Skip(list.Count - maxMessages).ToArray();
                }

                return list.ToArray();
            }
        }

        private sealed class FakeMemoryStore : IAgentMemoryStore
        {
            public string ClearedRole { get; private set; }

            public void Clear(string roleId)
            {
            }

            public void ClearChatHistory(string roleId)
            {
                ClearedRole = roleId;
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
            }

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = null;
                return false;
            }

            public void Save(string roleId, AgentMemoryState state)
            {
            }
        }

        private sealed class FakeAiOrchestrator : IAiOrchestrationService
        {
            private readonly string _content;
            private readonly string _error;
            private readonly string[] _streamChunks;

            public int CompleteCallCount { get; private set; }
            public int StreamingCallCount { get; private set; }

            public FakeAiOrchestrator(string content = "OK",
                string errorMessage = null,
                string[] streamChunks = null)
            {
                _content = content;
                _error = errorMessage;
                _streamChunks = streamChunks;
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                CompleteCallCount++;

                if (_error != null)
                {
                    throw new Exception(_error);
                }

                return Task.FromResult(_content ?? "");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                StreamingCallCount++;

                if (_error != null)
                {
                    yield return new LlmStreamChunk { IsDone = true, Error = _error };
                    yield break;
                }

                if (_streamChunks != null)
                {
                    foreach (string c in _streamChunks)
                    {
                        ct.ThrowIfCancellationRequested();
                        yield return new LlmStreamChunk { Text = c };
                        await Task.Yield();
                    }

                    yield return new LlmStreamChunk { IsDone = true };
                    yield break;
                }

                yield return new LlmStreamChunk { Text = _content ?? "" };
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string scopeId)
            {
            }
        }

        /// <summary>
        /// Captures the CancellationToken received by RunTaskAsync so tests can
        /// verify whether the service wraps it in a timeout-linked CTS.
        /// </summary>
        private sealed class TokenCapturingOrchestrator : IAiOrchestrationService
        {
            public CancellationToken LastCancellationToken { get; private set; }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                LastCancellationToken = ct;
                return Task.FromResult("ok");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                LastCancellationToken = ct;
                yield return new LlmStreamChunk { Text = "ok", IsDone = true };
                await Task.CompletedTask;
            }

            public void CancelTasks(string scopeId)
            {
            }
        }

        /// <summary>Blocks <see cref="RunTaskAsync"/> until <paramref name="ct"/> is cancelled (timeout or user).</summary>
        private sealed class BlockUntilCancelledOrchestrator : IAiOrchestrationService
        {
            public async Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "unreachable";
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                yield return new LlmStreamChunk { Text = "unreachable", IsDone = true };
            }

            public void CancelTasks(string scopeId)
            {
            }
        }

        /// <summary>
        /// Fake idle-deadline scheduler for <see cref="CoreAiChatService.IdleTimeoutDeadline.SchedulerOverride"/>:
        /// a deadline falls due on a clock the test advances by hand, so the idle window is measured in virtual
        /// time and no wall-clock margin is involved. A deadline whose handle was not disposed keeps counting,
        /// so a re-arm that forgets the old timer is caught exactly like a missing re-arm.
        /// </summary>
        private sealed class VirtualIdleClock
        {
            private readonly object _gate = new();
            private readonly List<Deadline> _armed = new();

            public TimeSpan Now { get; private set; }

            /// <summary>Earliest due time among armed, undisposed deadlines; null when nothing is armed.</summary>
            public TimeSpan? PendingDeadline
            {
                get
                {
                    lock (_gate)
                    {
                        TimeSpan? earliest = null;
                        foreach (Deadline deadline in _armed)
                        {
                            if (deadline.Active && (earliest == null || deadline.Due < earliest.Value))
                            {
                                earliest = deadline.Due;
                            }
                        }

                        return earliest;
                    }
                }
            }

            public IDisposable Schedule(CancellationTokenSource cts, TimeSpan window)
            {
                lock (_gate)
                {
                    Deadline deadline = new(cts, Now + window);
                    _armed.Add(deadline);
                    return deadline;
                }
            }

            public void Advance(TimeSpan delta)
            {
                List<Deadline> due = new();
                lock (_gate)
                {
                    Now += delta;
                    foreach (Deadline deadline in _armed)
                    {
                        if (deadline.Active && deadline.Due <= Now)
                        {
                            due.Add(deadline);
                        }
                    }
                }

                foreach (Deadline deadline in due)
                {
                    deadline.Fire();
                }
            }

            private sealed class Deadline : IDisposable
            {
                private readonly CancellationTokenSource _cts;

                public TimeSpan Due { get; }
                public bool Active { get; private set; } = true;

                public Deadline(CancellationTokenSource cts, TimeSpan due)
                {
                    _cts = cts;
                    Due = due;
                }

                public void Fire()
                {
                    Active = false;
                    _cts.Cancel();
                }

                public void Dispose()
                {
                    Active = false;
                }
            }
        }

        /// <summary>
        /// Waits for a test-released gate while honouring <paramref name="ct"/>, so a fired idle deadline
        /// surfaces as <see cref="OperationCanceledException"/> exactly as a cancelled transport would.
        /// </summary>
        private static async Task WaitForGateAsync(Task gate, CancellationToken ct)
        {
            TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetCanceled(ct)))
            {
                Task finished = await Task.WhenAny(gate, cancelled.Task);
                await finished;
            }
        }

        private static TaskCompletionSource<bool>[] CreateGates(int count)
        {
            TaskCompletionSource<bool>[] gates = new TaskCompletionSource<bool>[count];
            for (int i = 0; i < count; i++)
            {
                gates[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return gates;
        }

        /// <summary>
        /// Streams <paramref name="chunks"/> one per released gate; gate <c>chunks.Length</c> releases the
        /// terminal chunk, so the test decides when the turn ends.
        /// </summary>
        private sealed class GatedChunkOrchestrator : IAiOrchestrationService
        {
            private readonly string[] _chunks;
            private readonly TaskCompletionSource<bool>[] _gates;

            public GatedChunkOrchestrator(string[] chunks)
            {
                _chunks = chunks;
                _gates = CreateGates(chunks.Length + 1);
            }

            public void Release(int gate)
            {
                _gates[gate].TrySetResult(true);
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                throw new NotSupportedException();
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                for (int i = 0; i < _chunks.Length; i++)
                {
                    await WaitForGateAsync(_gates[i].Task, ct);
                    yield return new LlmStreamChunk { Text = _chunks[i] };
                }

                await WaitForGateAsync(_gates[_chunks.Length].Task, ct);
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string scopeId)
            {
            }
        }

        /// <summary>Yields one immediate chunk, then stalls forever — the idle timeout must still fire.</summary>
        private sealed class StallAfterFirstChunkOrchestrator : IAiOrchestrationService
        {
            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                throw new NotSupportedException();
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                yield return new LlmStreamChunk { Text = "first" };
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                yield return new LlmStreamChunk { Text = "unreachable", IsDone = true };
            }

            public void CancelTasks(string scopeId)
            {
            }
        }

        /// <summary>
        /// Simulates a multi-tool-call turn on the non-streaming path: fires
        /// <see cref="CoreAi.OnToolCallStarted"/>/<see cref="CoreAi.OnToolCallCompleted"/> for
        /// <see cref="AiTaskRequest.RoleId"/> around each step, each step blocking on a test-released gate.
        /// </summary>
        private sealed class GatedToolCallOrchestrator : IAiOrchestrationService
        {
            private readonly TaskCompletionSource<bool>[] _gates;

            public GatedToolCallOrchestrator(int steps)
            {
                _gates = CreateGates(steps);
            }

            /// <summary>Number of tool calls whose start event has already been raised.</summary>
            public int StartedSteps { get; private set; }

            public void Release(int step)
            {
                _gates[step].TrySetResult(true);
            }

            public async Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                for (int i = 0; i < _gates.Length; i++)
                {
                    CoreAi.NotifyToolCallStarted(new LlmToolCallStarted("trace", request.RoleId, $"tool{i}", "{}"));
                    StartedSteps = i + 1;
                    await WaitForGateAsync(_gates[i].Task, ct);
                    CoreAi.NotifyToolCallCompleted(new LlmToolCallCompleted(
                        "trace", request.RoleId, $"tool{i}", "{}", "{}", 0d));
                }

                return "done";
            }

            public IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                CancellationToken ct = default)
            {
                throw new NotSupportedException();
            }

            public void CancelTasks(string scopeId)
            {
            }
        }
    }
}
