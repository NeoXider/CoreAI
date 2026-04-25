using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode-тесты для <see cref="CoreAiChatService"/>:
    /// — иерархия вычисления флага стриминга (UI → per-agent → global);
    /// — SmartSend (автоматический выбор streaming/non-streaming);
    /// — базовые сценарии Send/Streaming с поддельным <see cref="IAiOrchestrationService"/>.
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
        public async Task SendMessageAsync_Error_ReturnsNull()
        {
            FakeAiOrchestrator orchestrator = new(null, errorMessage: "connection refused");
            CoreAiChatService service = new(orchestrator);

            string response = await service.SendMessageAsync("hi", "TestRole");
            Assert.IsNull(response);
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
            public float LlmRequestTimeoutSeconds => 15f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming { get; set; } = true;
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
    }
}
