using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// v1.5.5 tests for ARCH-3 (SanitizeAndPublish), ARCH-6 (BuildCompletionRequest),
    /// and ARCH-7 (#if UNITY removal).
    /// </summary>
    [TestFixture]
    public sealed class AiOrchestratorRefactorEditModeTests
    {
        #region Test doubles

        private sealed class CapturingLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }
            public int CallCount { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools) { }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                CallCount++;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }
        }

        private sealed class CapturingSink : IAiGameCommandSink
        {
            public ApplyAiGameCommand LastCommand { get; private set; }
            public int PublishCount { get; private set; }

            public void Publish(ApplyAiGameCommand command)
            {
                LastCommand = command;
                PublishCount++;
            }
        }

        private sealed class TestAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class DenyAiAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => false;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class FailingLlmClient : ILlmClient
        {
            public string ErrorMessage { get; set; } = "llm failed";

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = false,
                    Error = ErrorMessage
                });
            }
        }

        private static AiOrchestrator BuildWithAuth(IAuthorityHost auth, ILlmClient llm, CapturingSink sink = null)
        {
            return new AiOrchestrator(
                auth, llm, sink ?? new CapturingSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null),
                memoryStore: null, memoryPolicy: new AgentMemoryPolicy(),
                structuredPolicy: null, metrics: null, settings: new TestSettings());
        }

        private sealed class TestTelemetry : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot() => new();
        }

        private sealed class TestSettings : ICoreAISettings
        {
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 8192;
            public int MaxLlmRequestRetries => 1;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxToolCallRetries => 1;
            public bool AllowDuplicateToolCalls => false;
            public string UniversalSystemPromptPrefix => "";
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public int MaxLuaRepairRetries => 1;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }

        private sealed class NullSys : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt) { prompt = null; return false; }
        }

        private sealed class NullUsr : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template) { template = null; return false; }
        }

        private static AiOrchestrator Build(CapturingLlmClient llm, CapturingSink sink = null)
        {
            return BuildWithAuth(new TestAuthority(), llm, sink);
        }

        #endregion

        // ─────────────────────────────────────────────────
        // ARCH-6: BuildCompletionRequest — all fields forwarded
        // ─────────────────────────────────────────────────

        [Test]
        public async Task BuildCompletionRequest_ForwardsAllFields_Sync()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orch = Build(llm);

            await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Tester",
                Hint = "go",
                ForcedToolMode = LlmToolChoiceMode.RequireAny,
                RequiredToolName = "test_tool",
                MaxOutputTokens = 512,
                AllowedToolNames = new[] { "test_tool" }
            });

            Assert.IsNotNull(llm.LastRequest);
            Assert.AreEqual("Tester", llm.LastRequest.AgentRoleId);
            Assert.AreEqual(LlmToolChoiceMode.RequireAny, llm.LastRequest.ForcedToolMode);
            Assert.AreEqual("test_tool", llm.LastRequest.RequiredToolName);
            Assert.AreEqual(512, llm.LastRequest.MaxOutputTokens);
        }

        [Test]
        public async Task BuildCompletionRequest_ForwardsAllFields_Streaming()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orch = Build(llm);

            // Streaming also uses BuildCompletionRequest — the same LLM client
            // captures the request that goes into CompleteStreamingAsync, which
            // uses the DIM fallback to call CompleteAsync.
            await foreach (LlmStreamChunk _ in orch.RunStreamingAsync(new AiTaskRequest
            {
                RoleId = "Tester",
                Hint = "stream-go",
                ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                RequiredToolName = "quiz",
                MaxOutputTokens = 256
            }))
            {
                // consume all chunks
            }

            Assert.IsNotNull(llm.LastRequest, "Streaming path must call the LLM client.");
            Assert.AreEqual("Tester", llm.LastRequest.AgentRoleId);
            Assert.AreEqual(LlmToolChoiceMode.RequireSpecific, llm.LastRequest.ForcedToolMode);
            Assert.AreEqual("quiz", llm.LastRequest.RequiredToolName);
            Assert.AreEqual(256, llm.LastRequest.MaxOutputTokens);
        }

        // ─────────────────────────────────────────────────
        // ARCH-3: SanitizeAndPublish — publishes command
        // ─────────────────────────────────────────────────

        [Test]
        public async Task SanitizeAndPublish_Sync_PublishesCommandWithContent()
        {
            CapturingLlmClient llm = new();
            CapturingSink sink = new();
            AiOrchestrator orch = Build(llm, sink);

            string result = await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "R",
                Hint = "test-hint",
                SourceTag = "test-tag"
            });

            Assert.AreEqual("ok", result);
            Assert.AreEqual(1, sink.PublishCount, "Command must be published exactly once.");
            Assert.AreEqual("ok", sink.LastCommand.JsonPayload);
            Assert.AreEqual("R", sink.LastCommand.SourceRoleId);
            Assert.AreEqual("test-hint", sink.LastCommand.SourceTaskHint);
            Assert.AreEqual("test-tag", sink.LastCommand.SourceTag);
        }

        [Test]
        public async Task SanitizeAndPublish_Streaming_PublishesCommandWithContent()
        {
            CapturingLlmClient llm = new();
            CapturingSink sink = new();
            AiOrchestrator orch = Build(llm, sink);

            await foreach (LlmStreamChunk _ in orch.RunStreamingAsync(new AiTaskRequest
            {
                RoleId = "R",
                Hint = "stream-hint",
                SourceTag = "stream-tag"
            }))
            {
                // consume
            }

            Assert.AreEqual(1, sink.PublishCount, "Streaming path must also publish exactly once.");
            Assert.AreEqual("ok", sink.LastCommand.JsonPayload);
            Assert.AreEqual("R", sink.LastCommand.SourceRoleId);
            Assert.AreEqual("stream-hint", sink.LastCommand.SourceTaskHint);
        }

        [Test]
        public async Task RunTaskAsync_ChatSource_OnLlmFailure_ReturnsErrorText()
        {
            FailingLlmClient llm = new() { ErrorMessage = "HTTP 503" };
            AiOrchestrator orch = BuildWithAuth(new TestAuthority(), llm);

            string result = await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "hi",
                SourceTag = "Chat"
            });

            Assert.IsNotNull(result);
            StringAssert.Contains("503", result);
        }

        [Test]
        public async Task RunTaskAsync_NonChatSource_OnLlmFailure_ReturnsNull()
        {
            FailingLlmClient llm = new() { ErrorMessage = "HTTP 503" };
            AiOrchestrator orch = BuildWithAuth(new TestAuthority(), llm);

            string result = await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "hi",
                SourceTag = "Lua"
            });

            Assert.IsNull(result);
        }

        [Test]
        public async Task RunTaskAsync_ChatSource_AuthorityDenied_ReturnsMessage()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orch = BuildWithAuth(new DenyAiAuthority(), llm);

            string result = await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "SmartChat",
                Hint = "x",
                SourceTag = "Chat"
            });

            Assert.IsNotNull(result);
            StringAssert.Contains("disabled", result.ToLowerInvariant());
            Assert.AreEqual(0, llm.CallCount, "LLM must not run when authority denies.");
        }

        // ─────────────────────────────────────────────────
        // ARCH-7: DIM availability without #if UNITY
        // ─────────────────────────────────────────────────

        [Test]
        public async Task ILlmClient_DimFallback_IsAvailable()
        {
            // This test compiles and runs only if the #if UNITY guard was removed.
            // If the guard were still present, ILlmClient would not have
            // CompleteStreamingAsync in a non-Unity test runner.
            ILlmClient client = new CapturingLlmClient();
            int chunks = 0;
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(
                new LlmCompletionRequest
                {
                    AgentRoleId = "Test",
                    SystemPrompt = "sys",
                    UserPayload = "hi"
                }))
            {
                chunks++;
                if (chunk.IsDone) break;
            }

            Assert.GreaterOrEqual(chunks, 1, "DIM fallback must produce at least one chunk.");
        }

        [Test]
        public async Task IAiOrchestrationService_DimFallback_IsAvailable()
        {
            // Same as above for the orchestration interface.
            IAiOrchestrationService svc = new FallbackOnlyService();
            int chunks = 0;
            await foreach (LlmStreamChunk chunk in svc.RunStreamingAsync(new AiTaskRequest()))
            {
                chunks++;
            }

            Assert.AreEqual(2, chunks, "DIM fallback: 1 text + 1 terminal.");
        }

        private sealed class FallbackOnlyService : IAiOrchestrationService
        {
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken ct = default)
                => Task.FromResult("dim-result");

            public void CancelTasks(string scope) { }
        }
    }
}
