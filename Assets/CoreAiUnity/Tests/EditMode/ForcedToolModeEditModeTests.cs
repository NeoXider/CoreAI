using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode tests for the ForcedToolMode pipeline introduced in v0.25.0.
    /// Validates that <see cref="AiTaskRequest.ForcedToolMode"/> + <see cref="AiTaskRequest.RequiredToolName"/>
    /// reach <see cref="LlmCompletionRequest"/> verbatim through both the orchestrator and the
    /// streaming/structured-retry paths.
    ///
    /// Backend-level mapping to <c>ChatOptions.ToolMode</c> is exercised by the live LLM tests;
    /// here we only assert the in-process plumbing so the contract can't silently break.
    /// </summary>
    [TestFixture]
    public sealed class ForcedToolModeEditModeTests
    {
        private sealed class CapturingLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }

            public void SetTools(IReadOnlyList<ILlmTool> tools) { }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }
        }

        private sealed class TestAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class TestSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command) { }
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

        private sealed class StubTool : ILlmTool
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string ParametersSchema { get; set; } = "{}";
            public bool AllowDuplicates { get; set; }
        }

        private static AiOrchestrator BuildOrchestrator(CapturingLlmClient llm, AgentMemoryPolicy policy = null)
        {
            return new AiOrchestrator(
                new TestAuthority(), llm, new TestSink(), new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null),
                memoryStore: null, memoryPolicy: policy ?? new AgentMemoryPolicy(),
                structuredPolicy: null, metrics: null, settings: new TestSettings());
        }

        [Test]
        public void Defaults_AreAuto_AndEmptyName()
        {
            AiTaskRequest task = new AiTaskRequest();
            Assert.AreEqual(LlmToolChoiceMode.Auto, task.ForcedToolMode);
            Assert.AreEqual(string.Empty, task.RequiredToolName);

            LlmCompletionRequest req = new LlmCompletionRequest();
            Assert.AreEqual(LlmToolChoiceMode.Auto, req.ForcedToolMode);
            Assert.AreEqual(string.Empty, req.RequiredToolName);
        }

        [Test]
        public async Task RunTaskAsync_PropagatesForcedToolMode_RequireAny()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm);

            AiTaskRequest task = new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "make me a test",
                ForcedToolMode = LlmToolChoiceMode.RequireAny
            };

            await orchestrator.RunTaskAsync(task);

            Assert.IsNotNull(llm.LastRequest, "Orchestrator must have called the LLM client.");
            Assert.AreEqual(LlmToolChoiceMode.RequireAny, llm.LastRequest.ForcedToolMode,
                "ForcedToolMode must propagate verbatim from AiTaskRequest to LlmCompletionRequest.");
        }

        [Test]
        public async Task RunTaskAsync_PropagatesRequiredToolName_RequireSpecific()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Teacher",
                Hint = "spawn",
                ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                RequiredToolName = "spawn_quiz"
            });

            Assert.IsNotNull(llm.LastRequest);
            Assert.AreEqual(LlmToolChoiceMode.RequireSpecific, llm.LastRequest.ForcedToolMode);
            Assert.AreEqual("spawn_quiz", llm.LastRequest.RequiredToolName);
        }

        [Test]
        public async Task RunTaskAsync_DefaultsToAuto_WhenNothingSet()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "hi" });

            Assert.AreEqual(LlmToolChoiceMode.Auto, llm.LastRequest.ForcedToolMode,
                "Existing call sites that don't set ForcedToolMode must continue to behave as v0.24.x (Auto).");
        }

        [Test]
        public async Task RunTaskAsync_AppendsToolContract_WhenRoleHasTools()
        {
            AgentMemoryPolicy policy = new();
            policy.DisableMemoryTool("Merchant");
            policy.SetToolsForRole("Merchant", new ILlmTool[]
            {
                new StubTool
                {
                    Name = "buy_item",
                    Description = "Buy an item for the player.",
                    ParametersSchema = "{\"type\":\"object\",\"properties\":{\"itemName\":{\"type\":\"string\"},\"quantity\":{\"type\":\"integer\"}},\"required\":[\"itemName\",\"quantity\"]}"
                }
            });

            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, policy);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "Merchant",
                Hint = "buy potion",
                ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                RequiredToolName = "buy_item"
            });

            Assert.IsNotNull(llm.LastRequest);
            StringAssert.Contains("## Tool Contract", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("buy_item", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("do not claim that the tool is unavailable", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("This request requires calling tool 'buy_item'", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("itemName", llm.LastRequest.SystemPrompt);
        }

        [Test]
        public async Task RunTaskAsync_UsesPerAgentMaxOutputTokens_WhenPerCallMissing()
        {
            AgentMemoryPolicy policy = new();
            AgentConfig agent = new AgentBuilder("ShortNpc")
                .WithMaxOutputTokens(256)
                .Build();
            agent.ApplyToPolicy(policy);

            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, policy);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "ShortNpc", Hint = "hi" });

            Assert.IsNotNull(llm.LastRequest);
            Assert.AreEqual(256, llm.LastRequest.MaxOutputTokens,
                "Per-agent MaxOutputTokens must be forwarded when the call does not override it.");
        }

        [Test]
        public async Task RunTaskAsync_PerCallMaxOutputTokens_WinsOverPerAgent()
        {
            AgentMemoryPolicy policy = new();
            AgentConfig agent = new AgentBuilder("ShortNpc")
                .WithMaxOutputTokens(256)
                .Build();
            agent.ApplyToPolicy(policy);

            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, policy);

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = "ShortNpc",
                Hint = "hi",
                MaxOutputTokens = 99
            });

            Assert.IsNotNull(llm.LastRequest);
            Assert.AreEqual(99, llm.LastRequest.MaxOutputTokens,
                "Per-call MaxOutputTokens has higher priority than the agent default.");
        }

        [Test]
        public async Task RunTaskAsync_NoPerAgentMaxOutputTokens_LeavesRequestUnset()
        {
            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "DefaultNpc", Hint = "hi" });

            Assert.IsNotNull(llm.LastRequest);
            Assert.IsNull(llm.LastRequest.MaxOutputTokens,
                "Without per-call or per-agent override, the LLM client applies settings/provider fallback.");
        }
    }
}
