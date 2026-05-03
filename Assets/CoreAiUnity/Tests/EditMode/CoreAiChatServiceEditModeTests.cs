using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode-тесты для <see cref="CoreAiChatService"/>:
    /// — иерархия вычисления флага стриминга (UI → per-agent → global);
    /// — SmartSend (автоматический выбор streaming/non-streaming);
    /// — базовые сценарии Send/Streaming с поддельным <see cref="IAiOrchestrationService"/>;
    /// — восстановление сессии чата: <see cref="CoreAiChatService.TryGetPersistedChatHistory"/> и форматирование строк для UI.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatServiceEditModeTests
    {
        [SetUp]
        public void SetUp()
        {
            CoreAISettings.ResetOverrides();
            CoreAISettings.Instance = null;
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettings.ResetOverrides();
            CoreAISettings.Instance = null;
        }

        // ===================== Persisted chat (session restore for UI) =====================

        [Test]
        public void TryGetPersistedChatHistory_NoStore_ReturnsFalse()
        {
            CoreAiChatService service = new(new FakeAiOrchestrator("ok"), memoryStore: null);

            bool ok = service.TryGetPersistedChatHistory("SmartChat", out ChatMessage[] msgs, maxMessages: 0);

            Assert.IsFalse(ok);
            Assert.IsNotNull(msgs);
            Assert.AreEqual(0, msgs.Length);
        }

        [Test]
        public void TryGetPersistedChatHistory_EmptyHistory_ReturnsFalse()
        {
            var store = new ListBackedChatHistoryStore();
            CoreAiChatService service = new(new FakeAiOrchestrator("ok"), memoryStore: store);

            Assert.IsFalse(service.TryGetPersistedChatHistory("SmartChat", out ChatMessage[] msgs, 0));
        }

        [Test]
        public void TryGetPersistedChatHistory_ReturnsTailWhenMaxMessagesSet()
        {
            var store = new ListBackedChatHistoryStore();
            const string role = "SmartChat";
            for (int i = 0; i < 5; i++)
            {
                store.AppendChatMessage(role, i % 2 == 0 ? "user" : "assistant", $"m{i}", persistToDisk: false);
            }

            CoreAiChatService service = new(new FakeAiOrchestrator("ok"), memoryStore: store);

            Assert.IsTrue(service.TryGetPersistedChatHistory(role, out ChatMessage[] msgs, maxMessages: 2));
            Assert.AreEqual(2, msgs.Length);
            Assert.AreEqual("m3", msgs[0].Content);
            Assert.AreEqual("m4", msgs[1].Content);
        }

        [Test]
        public void PersistedChat_UiFormattingRoundTrip_MatchesCoreAiChatPanelRules()
        {
            var store = new ListBackedChatHistoryStore();
            const string role = "SmartChat";
            string userComposer =
                "{\"telemetry\":{},\"hint\":\"stored user line\",\"ai_task_source\":\"Chat\"}";
            store.AppendChatMessage(role, "user", userComposer, persistToDisk: false);
            store.AppendChatMessage(role, "assistant", "visible reply", persistToDisk: false);

            CoreAiChatService service = new(new FakeAiOrchestrator("ok"), memoryStore: store);
            Assert.IsTrue(service.TryGetPersistedChatHistory(role, out ChatMessage[] msgs, 0));
            Assert.AreEqual(2, msgs.Length);

            string userLine = CoreAiChatPanel.FormatPersistedMessageForUi(msgs[0].Content, isUser: true);
            string assistantLine = CoreAiChatPanel.FormatPersistedMessageForUi(msgs[1].Content, isUser: false);

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
                memoryPolicy: null,
                settings: settings);

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
                memoryPolicy: policy,
                settings: settings);

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
                memoryPolicy: policy,
                settings: settings);

            // UI слой выключил стриминг → всё остальное игнорируется
            Assert.IsFalse(service.IsStreamingEnabled("Role", uiOverride: false));
        }

        [Test]
        public void IsStreamingEnabled_UiOverrideFalse_ForcesOff()
        {
            StubSettings settings = new() { EnableStreaming = true };
            CoreAiChatService service = new(new FakeAiOrchestrator("ok"),
                memoryPolicy: null,
                settings: settings);

            // Перегрузка bool?: false выключает, true/null — обычное разрешение
            Assert.IsFalse(service.IsStreamingEnabled("Role", uiOverride: (bool?)false));
            Assert.IsTrue(service.IsStreamingEnabled("Role", uiOverride: (bool?)true));
            Assert.IsTrue(service.IsStreamingEnabled("Role", uiOverride: (bool?)null));
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
        public void SendMessageAsync_Error_PropagatesException()
        {
            // v1.5.1: CoreAiChatService no longer swallows exceptions.
            // Errors propagate to the caller (CoreAiChatPanel), which displays them.
            FakeAiOrchestrator orchestrator = new(null, errorMessage: "connection refused");
            CoreAiChatService service = new(orchestrator);

            var ex = Assert.ThrowsAsync<System.Exception>(
                async () => await service.SendMessageAsync("hi", "TestRole"));
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
                if (!string.IsNullOrEmpty(chunk.Text)) visible.Add(chunk.Text);
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
                memoryPolicy: null,
                settings: settings);

            List<string> chunks = new();
            string full = await service.SendMessageSmartAsync(
                "hi", "Role",
                onChunk: c => { if (!string.IsNullOrEmpty(c.Text)) chunks.Add(c.Text); });

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
                memoryPolicy: null,
                settings: settings);

            List<string> chunks = new();
            string full = await service.SendMessageSmartAsync(
                "hi", "Role",
                onChunk: c => { if (!string.IsNullOrEmpty(c.Text)) chunks.Add(c.Text); });

            Assert.AreEqual("Full response text", full);
            Assert.AreEqual(1, orchestrator.CompleteCallCount);
            Assert.AreEqual(0, orchestrator.StreamingCallCount);

            // onChunk должен быть вызван даже в non-streaming пути: 1 чанк с текстом + финал
            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual("Full response text", chunks[0]);
        }

        [Test]
        public async Task SendSmart_UiOverrideFalse_ForcesNonStreaming()
        {
            FakeAiOrchestrator orchestrator = new("Non-streaming answer");
            StubSettings settings = new() { EnableStreaming = true };
            CoreAiChatService service = new(orchestrator,
                memoryPolicy: null,
                settings: settings);

            string full = await service.SendMessageSmartAsync(
                "hi", "Role",
                onChunk: null,
                uiStreamingOverride: false);

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
            
            // В EditMode нет CoreAILifetimeScope — StopAgent должен отработать молча (graceful degradation).
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
            FakeAiOrchestrator orchestrator = new(content: null);
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
        [Timeout(8000)]
        public IEnumerator SendMessageAsync_TimeoutWhenOrchestratorBlocks_ThrowsLlmOperationTimeoutException()
        {
            BlockUntilCancelledOrchestrator orchestrator = new();
            StubSettings settings = new() { LlmRequestTimeoutSecondsOverride = 0.2f };
            CoreAiChatService service = new(orchestrator, settings: settings);

            Task task = service.SendMessageAsync("hi", "TestRole");

            float deadline = Time.realtimeSinceStartup + 6f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsTrue(task.IsCompleted, "SendMessageAsync should complete once the timeout token fires.");
            // LlmOperationTimeoutException inherits OperationCanceledException — async Task reports
            // TaskStatus.Canceled (not Faulted) and task.Exception is null; unwrap via GetResult().
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

            public void ClearChatHistory(string roleId) => _history.Remove(roleId);

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
            
            public void Clear(string roleId) { }
            public void ClearChatHistory(string roleId) => ClearedRole = roleId;
            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true) { }
            public CoreAI.Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => System.Array.Empty<CoreAI.Ai.ChatMessage>();
            public bool TryLoad(string roleId, out CoreAI.Ai.AgentMemoryState state) { state = null; return false; }
            public void Save(string roleId, CoreAI.Ai.AgentMemoryState state) { }
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
                    throw new System.Exception(_error);
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

            public void CancelTasks(string scopeId) { }
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

            public void CancelTasks(string scopeId) { }
        }

        /// <summary>Blocks <see cref="RunTaskAsync"/> until <paramref name="ct"/> is cancelled (timeout or user).</summary>
        private sealed class BlockUntilCancelledOrchestrator : IAiOrchestrationService
        {
            public async Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return "unreachable";
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                yield return new LlmStreamChunk { Text = "unreachable", IsDone = true };
            }

            public void CancelTasks(string scopeId) { }
        }
    }
}
