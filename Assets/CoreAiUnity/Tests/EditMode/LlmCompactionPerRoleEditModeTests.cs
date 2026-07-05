using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class LlmCompactionPerRoleEditModeTests
    {
        private sealed class TestAuthority : IAuthorityHost
        {
            public bool CanRunAiTasks => true;
            public bool IsServer => true;
            public bool IsClient => true;
        }

        private sealed class FlatTokenEstimator : ITokenEstimator
        {
            private readonly int _n;

            public FlatTokenEstimator(int n)
            {
                _n = Math.Max(1, n);
            }

            public int EstimateText(string text)
            {
                return _n;
            }
        }

        private sealed class FixedHistoryBudgetPolicy : IContextBudgetPolicy
        {
            private readonly int _historyBudget;

            public FixedHistoryBudgetPolicy(int historyBudget)
            {
                _historyBudget = Math.Max(1, historyBudget);
            }

            public ContextBudget Compute(ContextBudgetRequest request, ITokenEstimator estimator)
            {
                return new ContextBudget(8192, 256, 50, _historyBudget, 0);
            }
        }

        /// <summary>Counts compaction-role calls separately from orchestrator/main LLM calls.</summary>
        private sealed class SplitCountingLlm : ILlmClient
        {
            private readonly ILlmClient _inner;
            public int CompactionCompletes { get; private set; }

            public SplitCountingLlm(ILlmClient inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                if (string.Equals(
                        request.AgentRoleId,
                        BuiltInAgentRoleIds.ContextCompactionAux,
                        StringComparison.Ordinal))
                {
                    CompactionCompletes++;
                    return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "rollup-text" });
                }

                return _inner.CompleteAsync(request, cancellationToken);
            }
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            public List<ChatMessage> FakeHistory { get; } = new();

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
            }

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                if (maxMessages > 0 && FakeHistory.Count > maxMessages)
                {
                    int skip = FakeHistory.Count - maxMessages;
                    return FakeHistory.GetRange(skip, maxMessages).ToArray();
                }

                return FakeHistory.ToArray();
            }
        }

        private sealed class CapturingContextManager : IConversationContextManager
        {
            public int BuildCalls { get; private set; }
            public ConversationContextBuildArgs LastBuildArgs { get; private set; }
            public AgentMemoryPolicy.RoleMemoryConfig LastRoleConfig { get; private set; }

            public ConversationContextSnapshot BuildSnapshot(
                string roleId,
                ChatMessage[] history,
                AgentMemoryPolicy.RoleMemoryConfig roleConfig,
                ConversationContextBuildArgs buildArgs = null)
            {
                BuildCalls++;
                LastRoleConfig = roleConfig;
                LastBuildArgs = buildArgs;
                return new ConversationContextSnapshot
                {
                    RecentMessages = history ?? Array.Empty<ChatMessage>()
                };
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
                prompt = "";
                return true;
            }
        }

        private sealed class NullUsr : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = "{hint}";
                return true;
            }
        }

        private sealed class StubCoreSettingsWithCompaction : ICoreAISettings
        {
            public bool EnableLlmContextCompaction => true;
            public int MaxLuaRepairRetries => 1;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 60f;
            public int MaxLlmRequestRetries => 1;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 8192;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int MaxToolCallRetries => 2;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => false;
            public float ConversationCompactionTriggerRatio { get; set; } = 0.8f;
        }

        private static AgentMemoryPolicy MakePolicyForRole(string roleId, bool chatHistory = true)
        {
            AgentMemoryPolicy policy = new();
            policy.DisableMemoryTool(roleId);
            policy.SetToolsForRole(roleId, Array.Empty<ILlmTool>());
            if (chatHistory)
            {
                policy.ConfigureChatHistory(roleId, true, 8192, false, 50);
            }

            return policy;
        }

        private static void SeedHistory(TestMemoryStore mem, int count)
        {
            for (int i = 0; i < count; i++)
            {
                mem.FakeHistory.Add(new ChatMessage
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = $"m{i}-".PadRight(40, 'x')
                });
            }
        }

        [Test]
        public void AgentMemoryPolicy_Programmer_DisablesLlmCompaction_ByDefault()
        {
            AgentMemoryPolicy policy = new();
            Assert.IsFalse(policy.GetRoleConfig(BuiltInAgentRoleIds.Programmer).UseLlmContextCompaction);
        }

        [Test]
        public void AgentMemoryPolicy_Creator_EnablesLlmCompaction_ByDefault()
        {
            AgentMemoryPolicy policy = new();
            Assert.IsTrue(policy.GetRoleConfig(BuiltInAgentRoleIds.Creator).UseLlmContextCompaction);
        }

        [Test]
        public void AgentBuilder_WithLlmContextCompaction_False_WrittenToPolicy()
        {
            AgentMemoryPolicy policy = new();
            new AgentBuilder("CustomNoSmart")
                .WithSystemPrompt("x")
                .WithChatHistory(4096)
                .WithLlmContextCompaction(false)
                .Build()
                .ApplyToPolicy(policy);

            Assert.IsFalse(policy.GetRoleConfig("CustomNoSmart").UseLlmContextCompaction);
        }

        [Test]
        public async Task Orchestrator_PerRole_UseLlmContextCompaction_CompactionCallOnlyWhenEnabled()
        {
            const string smartRole = "smart_role_compact";
            string programmerRole = BuiltInAgentRoleIds.Programmer;

            TestMemoryStore memSmart = new();
            SeedHistory(memSmart, 8);

            TestMemoryStore memProg = new();
            SeedHistory(memProg, 8);

            StubLlmClient stub = new();
            SplitCountingLlm counting = new(stub);

            InMemoryConversationSummaryStore summaryStore = new();
            ITokenEstimator compactEstimator = new FlatTokenEstimator(10);
            IConversationContextManager ctxMgr = ConversationContextManagerFactories.Create(
                true,
                summaryStore,
                compactEstimator,
                counting,
                null);

            StubCoreSettingsWithCompaction settings = new();

            AgentMemoryPolicy policySmart = MakePolicyForRole(smartRole);
            AiOrchestrator orchSmart = new(
                new TestAuthority(),
                counting,
                new TestSink(),
                new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policySmart, settings),
                memSmart,
                policySmart,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                ctxMgr,
                null,
                new FixedHistoryBudgetPolicy(25));

            await orchSmart.RunTaskAsync(new AiTaskRequest { RoleId = smartRole, Hint = "hi" }).ConfigureAwait(false);
            Assert.GreaterOrEqual(counting.CompactionCompletes, 1, "Smart role should trigger auxiliary compaction.");

            StubLlmClient stub2 = new();
            SplitCountingLlm counting2 = new(stub2);
            IConversationContextManager ctxMgr2 = ConversationContextManagerFactories.Create(
                true,
                new InMemoryConversationSummaryStore(),
                compactEstimator,
                counting2,
                null);

            AgentMemoryPolicy policyProg = MakePolicyForRole(programmerRole);
            AiOrchestrator orchProg = new(
                new TestAuthority(),
                counting2,
                new TestSink(),
                new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, policyProg, settings),
                memProg,
                policyProg,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                ctxMgr2,
                null,
                new FixedHistoryBudgetPolicy(25));

            await orchProg.RunTaskAsync(new AiTaskRequest { RoleId = programmerRole, Hint = "hi" })
                .ConfigureAwait(false);
            Assert.AreEqual(
                0,
                counting2.CompactionCompletes,
                "Programmer role should skip LLM compaction by default.");
        }

        [Test]
        public async Task Orchestrator_PerRole_CompactionTriggerRatio_OverridesGlobalRatio()
        {
            const string globalRole = "global_threshold_role";
            const string overrideRole = "override_threshold_role";
            StubCoreSettingsWithCompaction settings = new() { ConversationCompactionTriggerRatio = 0.8f };

            TestMemoryStore globalMem = new();
            SeedHistory(globalMem, 2);
            CapturingContextManager globalCtxMgr = new();
            AgentMemoryPolicy globalPolicy = MakePolicyForRole(globalRole);
            AiOrchestrator globalOrchestrator = new(
                new TestAuthority(),
                new StubLlmClient(),
                new TestSink(),
                new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, globalPolicy, settings),
                globalMem,
                globalPolicy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                globalCtxMgr,
                null,
                new FixedHistoryBudgetPolicy(25));

            await globalOrchestrator.RunTaskAsync(new AiTaskRequest { RoleId = globalRole, Hint = "hi" })
                .ConfigureAwait(false);
            Assert.AreEqual(1, globalCtxMgr.BuildCalls);
            Assert.IsNotNull(globalCtxMgr.LastBuildArgs);
            Assert.AreEqual(
                0.8f,
                globalCtxMgr.LastBuildArgs.CompactionTriggerRatio,
                "Global ratio should be used when the role has no override.");
            Assert.IsNull(globalCtxMgr.LastRoleConfig.CompactionTriggerRatio);

            TestMemoryStore overrideMem = new();
            SeedHistory(overrideMem, 2);
            CapturingContextManager overrideCtxMgr = new();
            AgentMemoryPolicy overridePolicy = MakePolicyForRole(overrideRole);
            overridePolicy.SetCompactionTriggerRatio(overrideRole, 1f);
            AiOrchestrator overrideOrchestrator = new(
                new TestAuthority(),
                new StubLlmClient(),
                new TestSink(),
                new TestTelemetry(),
                new AiPromptComposer(new NullSys(), new NullUsr(), null, null, overridePolicy, settings),
                overrideMem,
                overridePolicy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                overrideCtxMgr,
                null,
                new FixedHistoryBudgetPolicy(25));

            await overrideOrchestrator.RunTaskAsync(new AiTaskRequest { RoleId = overrideRole, Hint = "hi" })
                .ConfigureAwait(false);
            Assert.AreEqual(
                1,
                overrideCtxMgr.BuildCalls);
            Assert.IsNotNull(overrideCtxMgr.LastBuildArgs);
            Assert.AreEqual(
                1f,
                overrideCtxMgr.LastBuildArgs.CompactionTriggerRatio,
                "Per-role ratio should override the global ratio for this request.");
            Assert.AreEqual(1f, overrideCtxMgr.LastRoleConfig.CompactionTriggerRatio);
        }
    }
}