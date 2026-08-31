using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    public sealed class DeterministicToolContractEditModeTests
    {
        [Test]
        public async Task BuildRequest_SameToolsDifferentInsertionOrder_RendersCanonicalPrefix()
        {
            CapturingLlmClient firstLlm = new();
            CapturingLlmClient secondLlm = new();

            AgentMemoryPolicy firstPolicy = BuildPolicy(new StubTool("z_tool"), new StubTool("a_tool"));
            AgentMemoryPolicy secondPolicy = BuildPolicy(new StubTool("a_tool"), new StubTool("z_tool"));
            TestSettings firstSettings = new();
            TestSettings secondSettings = new();

            AiOrchestrator first = BuildOrchestrator(firstLlm, firstPolicy, firstSettings);
            AiOrchestrator second = BuildOrchestrator(secondLlm, secondPolicy, secondSettings);

            await first.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "same input" });
            await second.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "same input" });

            Assert.AreEqual(firstLlm.LastRequest.SystemPrompt, secondLlm.LastRequest.SystemPrompt);
            CollectionAssert.AreEqual(
                new[] { "a_tool", "memory", "z_tool" },
                AiToolOrder.Canonical(firstLlm.LastRequest.Tools).Select(t => t.Name).ToArray());
            CollectionAssert.AreEqual(
                AiToolOrder.Canonical(firstLlm.LastRequest.Tools).Select(t => t.Name).ToArray(),
                AiToolOrder.Canonical(secondLlm.LastRequest.Tools).Select(t => t.Name).ToArray());
            AssertNoGuidOrTimestamp(firstLlm.LastRequest.SystemPrompt);
            AssertNoGuidOrTimestamp(secondLlm.LastRequest.SystemPrompt);
        }

        [Test]
        public void AppendToolContract_CanonicalizesSchemaObjectKeysRecursively()
        {
            const string schemaA =
                "{\"type\":\"object\",\"required\":[\"b\",\"a\"],\"properties\":{\"b\":{\"type\":\"string\",\"description\":\"B\"},\"a\":{\"type\":\"number\",\"description\":\"A\"}}}";
            const string schemaB =
                "{\"properties\":{\"a\":{\"description\":\"A\",\"type\":\"number\"},\"b\":{\"description\":\"B\",\"type\":\"string\"}},\"required\":[\"b\",\"a\"],\"type\":\"object\"}";

            string first = AiToolContractPromptFormatter.AppendToolContract(
                "sys",
                new[] { new StubTool("same_tool", schemaA) },
                new AiTaskRequest { RoleId = "Teacher" },
                new TestSettings());
            string second = AiToolContractPromptFormatter.AppendToolContract(
                "sys",
                new[] { new StubTool("same_tool", schemaB) },
                new AiTaskRequest { RoleId = "Teacher" },
                new TestSettings());

            Assert.AreEqual(first, second);
            StringAssert.Contains(
                "schema: {\"properties\":{\"a\":{\"description\":\"A\",\"type\":\"number\"},\"b\":{\"description\":\"B\",\"type\":\"string\"}},\"required\":[\"b\",\"a\"],\"type\":\"object\"}",
                first);
        }

        [Test]
        public async Task BuildRequest_FixedInputs_ProduceIdenticalSystemPrefixWithoutGeneratedIds()
        {
            AgentMemoryPolicy policy = BuildPolicy(new StubTool("z_tool"), new StubTool("a_tool"));
            TestSettings settings = new();
            CapturingLlmClient llm = new();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, policy, settings);

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "same input" });
            string first = llm.LastRequest.SystemPrompt;

            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = "Teacher", Hint = "same input" });
            string second = llm.LastRequest.SystemPrompt;

            Assert.AreEqual(first, second);
            AssertNoGuidOrTimestamp(first);
        }

        [Test]
        public void AppendToolContract_NativeToolCallingWithMemoryTool_IncludesMemoryImperative()
        {
            // Regression: the memory instruction used to live only on the text-shaped path, after the
            // native early-return, so native tool-calling roles (e.g. Creator) got no memory guidance and
            // ignored "remember the ..." tasks.
            string native = AiToolContractPromptFormatter.AppendToolContract(
                "sys",
                new[] { new StubTool("memory") },
                new AiTaskRequest { RoleId = "Creator" },
                new TestSettings(),
                true);

            StringAssert.Contains("call the memory tool", native,
                "Native tool-calling roles with the memory tool must receive the positive memory instruction.");
        }

        [Test]
        public void AppendToolContract_NativeToolCallingWithoutMemoryTool_OmitsMemoryImperative()
        {
            string native = AiToolContractPromptFormatter.AppendToolContract(
                "sys",
                new[] { new StubTool("world_command") },
                new AiTaskRequest { RoleId = "Creator" },
                new TestSettings(),
                true);

            StringAssert.DoesNotContain("call the memory tool", native,
                "Roles without the memory tool must not receive memory guidance.");
        }

        private static AgentMemoryPolicy BuildPolicy(params ILlmTool[] tools)
        {
            AgentMemoryPolicy policy = new();
            policy.SetToolsForRole("Teacher", tools);
            policy.SetRuntimeContextProvider("Teacher", new TraceEchoRuntimeContextProvider());
            return policy;
        }

        private static AiOrchestrator BuildOrchestrator(
            ILlmClient llm,
            AgentMemoryPolicy policy,
            TestSettings settings)
        {
            return new AiOrchestrator(
                new TestAuthority(),
                llm,
                new TestSink(),
                new TestTelemetry(),
                new AiPromptComposer(new StaticSystemPromptProvider(), new NullUserPromptProvider(), null, null,
                    policy, settings),
                new TestMemoryStore(),
                policy,
                null,
                null,
                settings,
                new LocalActorIdentityProvider("tool-contract-test"));
        }

        private static void AssertNoGuidOrTimestamp(string value)
        {
            Assert.IsFalse(Regex.IsMatch(value ?? "", @"\b[0-9a-fA-F]{32}\b"),
                "Generated compact GUIDs must not appear in the frozen system prefix.");
            Assert.IsFalse(Regex.IsMatch(value ?? "", @"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}"),
                "Timestamp-shaped values must not appear in the frozen system prefix.");
        }

        private sealed class StubTool : ILlmTool
        {
            public StubTool(string name, string schema = "{}")
            {
                Name = name;
                ParametersSchema = schema;
            }

            public string Name { get; }
            public string Description => "stub tool";
            public string ParametersSchema { get; }
            public bool AllowDuplicates => false;
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
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

        private sealed class StaticSystemPromptProvider : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = "Stable teacher prompt.";
                return true;
            }
        }

        private sealed class NullUserPromptProvider : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = null;
                return false;
            }
        }

        private sealed class TraceEchoRuntimeContextProvider : IAgentRuntimeContextProvider
        {
            public string BuildContext(AiTaskRequest request, string roleId, string traceId)
            {
                return "trace=" + traceId;
            }
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
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
                return Array.Empty<ChatMessage>();
            }
        }

        private sealed class TestSettings : ICoreAISettings
        {
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 8192;
            public int MaxLlmRequestRetries => 1;
            public int MaxContextOverflowRetries => 3;
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
    }
}
