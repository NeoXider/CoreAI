using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace CoreAI.Tests.EditMode
{
    public sealed class PromptCacheLayeringEditModeTests
    {
        private const string RoleId = "Teacher";

        [Test]
        public async Task DifferentStudentContexts_KeepSharedPrefixByteIdentical_AndUseOrderedSystemTail()
        {
            CapturingLlmClient firstLlm = new();
            CapturingLlmClient secondLlm = new();
            AgentMemoryPolicy firstPolicy = BuildPolicy();
            AgentMemoryPolicy secondPolicy = BuildPolicy();

            AiOrchestrator first = BuildOrchestrator(
                firstLlm,
                firstPolicy,
                new TestMemoryStore(
                    "Student A remembers fractions.",
                    new ChatMessage("user", "Student A earlier question."),
                    new ChatMessage("assistant", "Student A earlier answer.")));
            AiOrchestrator second = BuildOrchestrator(
                secondLlm,
                secondPolicy,
                new TestMemoryStore(
                    "Student B remembers geometry.",
                    new ChatMessage("user", "Student B earlier question."),
                    new ChatMessage("assistant", "Student B earlier answer.")));

            await first.RunTaskAsync(new AiTaskRequest
            {
                RoleId = RoleId,
                RequestSystemInstructions = "Adapt explanations for Student A.",
                Hint = "Explain fractions.",
                SourceTag = "world-a",
                AllowedToolNames = new[] { "tool_alpha" },
                ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                RequiredToolName = "tool_alpha",
                MaxOutputTokens = 111,
                MaxToolCallRoundtrips = 3
            });
            await second.RunTaskAsync(new AiTaskRequest
            {
                RoleId = RoleId,
                RequestSystemInstructions = "Adapt explanations for Student B.",
                Hint = "Explain geometry.",
                SourceTag = "world-b",
                AllowedToolNames = new[] { "tool_beta" },
                ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                RequiredToolName = "tool_beta",
                MaxOutputTokens = 222,
                MaxToolCallRoundtrips = 7
            });

            LlmCompletionRequest firstRequest = firstLlm.LastRequest;
            LlmCompletionRequest secondRequest = secondLlm.LastRequest;
            Assert.IsNotNull(firstRequest);
            Assert.IsNotNull(secondRequest);
            Assert.AreEqual(firstRequest.SystemPrompt, secondRequest.SystemPrompt,
                "Per-student state or request tool filtering changed the provider-cache prefix.");
            StringAssert.Contains("Universal shared rules.", firstRequest.SystemPrompt);
            StringAssert.Contains("Stable teacher role instructions.", firstRequest.SystemPrompt);
            StringAssert.Contains("Role tool definitions:", firstRequest.SystemPrompt);
            StringAssert.Contains("tool_alpha", firstRequest.SystemPrompt);
            StringAssert.Contains("alpha_field", firstRequest.SystemPrompt);
            StringAssert.Contains("tool_beta", firstRequest.SystemPrompt);
            StringAssert.Contains("beta_field", firstRequest.SystemPrompt);
            StringAssert.DoesNotContain("Student A", firstRequest.SystemPrompt);
            StringAssert.DoesNotContain("Student B", firstRequest.SystemPrompt);
            StringAssert.DoesNotContain("world-a", firstRequest.SystemPrompt);
            StringAssert.DoesNotContain("world-b", firstRequest.SystemPrompt);
            StringAssert.DoesNotContain("Student A earlier question.", firstRequest.SystemPrompt);
            StringAssert.DoesNotContain("Student B earlier question.", firstRequest.SystemPrompt);
            StringAssert.DoesNotContain("## Tool Availability (current request)", firstRequest.SystemPrompt);

            AssertOrderedTail(
                firstRequest,
                "Adapt explanations for Student A.",
                "Student A remembers fractions.",
                "tool_alpha",
                "tool_beta",
                "world-a");
            AssertOrderedTail(
                secondRequest,
                "Adapt explanations for Student B.",
                "Student B remembers geometry.",
                "tool_beta",
                "tool_alpha",
                "world-b");

            CollectionAssert.AreEqual(new[] { "tool_alpha" }, firstRequest.Tools.Select(tool => tool.Name));
            CollectionAssert.AreEqual(new[] { "tool_beta" }, secondRequest.Tools.Select(tool => tool.Name));
            StringAssert.Contains("Student A earlier question.", firstRequest.ChatHistory[0].Text);
            StringAssert.Contains("Student B earlier question.", secondRequest.ChatHistory[0].Text);
            Assert.AreEqual(111, firstRequest.MaxOutputTokens);
            Assert.AreEqual(222, secondRequest.MaxOutputTokens);
            Assert.AreEqual(3, firstRequest.MaxToolCallRoundtrips);
            Assert.AreEqual(7, secondRequest.MaxToolCallRoundtrips);
        }

        [Test]
        public async Task NativeBackend_KeepsFullSharedRoleContract_ButFiltersNativeRequestTools()
        {
            CapturingLlmClient llm = new() { SupportsNativeToolCalling = true };
            AgentMemoryPolicy policy = BuildPolicy();
            AiOrchestrator orchestrator = BuildOrchestrator(
                llm,
                policy,
                new TestMemoryStore("Student C remembers equations."));

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = RoleId,
                RequestSystemInstructions = "Use the current lesson pace.",
                Hint = "Solve an equation.",
                SourceTag = "world-c",
                AllowedToolNames = new[] { "tool_beta" },
                ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                RequiredToolName = "tool_beta"
            });

            StringAssert.Contains("Role tool definitions:", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("tool_alpha", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("alpha_field", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("tool_beta", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("beta_field", llm.LastRequest.SystemPrompt);
            CollectionAssert.AreEqual(new[] { "tool_beta" }, llm.LastRequest.Tools.Select(tool => tool.Name));
            AssertOrderedTail(
                llm.LastRequest,
                "Use the current lesson pace.",
                "Student C remembers equations.",
                "tool_beta",
                "tool_alpha",
                "world-c");
        }

        [Test]
        public async Task LegacySystemPrompt_StillReplacesRoleBase_WhileRequestInstructionsStayInTail()
        {
            CapturingLlmClient llm = new();
            AgentMemoryPolicy policy = BuildPolicy();
            AiOrchestrator orchestrator = BuildOrchestrator(llm, policy, new TestMemoryStore(""));

            await orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = RoleId,
                SystemPrompt = "Legacy request role override.",
                RequestSystemInstructions = "Current student guidance.",
                Hint = "Explain fractions."
            });

            StringAssert.Contains("Universal shared rules.", llm.LastRequest.SystemPrompt);
            StringAssert.Contains("Legacy request role override.", llm.LastRequest.SystemPrompt);
            StringAssert.DoesNotContain("Stable teacher role instructions.", llm.LastRequest.SystemPrompt);
            StringAssert.DoesNotContain("Current student guidance.", llm.LastRequest.SystemPrompt);
            Assert.IsTrue(llm.LastRequest.ChatHistory.Any(message =>
                message.Role == ChatRole.System &&
                (message.Text ?? "").Contains("Current student guidance.", StringComparison.Ordinal)));
        }

        [Test]
        public void StableRoleToolContract_PreservesLargeCanonicalJsonSchemaWithoutTruncation()
        {
            JObject propertiesAscending = new();
            JObject propertiesDescending = new();
            for (int i = 0; i < 48; i++)
            {
                string name = "property_" + i.ToString("D2");
                propertiesAscending.Add(name, new JObject
                {
                    ["type"] = "string",
                    ["description"] = new string((char)('a' + i % 26), 24)
                });
            }

            foreach (JProperty property in propertiesAscending.Properties().Reverse())
            {
                propertiesDescending.Add(property.Name, property.Value.DeepClone());
            }

            JObject firstSchema = new()
            {
                ["type"] = "object",
                ["required"] = new JArray("property_47"),
                ["properties"] = propertiesAscending
            };
            JObject secondSchema = new()
            {
                ["properties"] = propertiesDescending,
                ["required"] = new JArray("property_47"),
                ["type"] = "object"
            };
            TestSettings settings = new();

            string first = AiToolContractPromptFormatter.AppendStableRoleToolContract(
                "stable",
                new ILlmTool[] { new StubTool("large_tool", firstSchema.ToString()) },
                settings);
            string second = AiToolContractPromptFormatter.AppendStableRoleToolContract(
                "stable",
                new ILlmTool[] { new StubTool("large_tool", secondSchema.ToString()) },
                settings);

            string firstJson = ExtractSchemaJson(first);
            string secondJson = ExtractSchemaJson(second);
            Assert.Greater(firstJson.Length, 800, "Fixture must cross the former truncation boundary.");
            Assert.AreEqual(firstJson, secondJson, "Object key order must not change shared-prefix bytes.");
            JObject parsed = JObject.Parse(firstJson);
            Assert.AreEqual("property_47", parsed["required"]?[0]?.Value<string>());
            Assert.AreEqual("string", parsed["properties"]?["property_47"]?["type"]?.Value<string>());
        }

        private static string ExtractSchemaJson(string contract)
        {
            const string marker = "  schema: ";
            int start = contract.IndexOf(marker, StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0);
            start += marker.Length;
            int end = contract.IndexOf('\n', start);
            return (end < 0 ? contract.Substring(start) : contract.Substring(start, end - start)).TrimEnd('\r');
        }

        private static void AssertOrderedTail(
            LlmCompletionRequest request,
            string requestInstructions,
            string memory,
            string availableTool,
            string unavailableTool,
            string worldState)
        {
            Assert.IsNotNull(request.ChatHistory);
            Assert.GreaterOrEqual(request.ChatHistory.Count, 4);
            Microsoft.Extensions.AI.ChatMessage[] tail = request.ChatHistory
                .Skip(request.ChatHistory.Count - 4)
                .ToArray();
            Assert.IsTrue(tail.All(message => message.Role == ChatRole.System));

            StringAssert.StartsWith("## Request System Instructions", tail[0].Text);
            StringAssert.Contains(requestInstructions, tail[0].Text);
            StringAssert.StartsWith("## Memory", tail[1].Text);
            StringAssert.Contains(memory, tail[1].Text);
            StringAssert.StartsWith("## Tool Availability (current request)", tail[2].Text);
            StringAssert.Contains("Available tools:", tail[2].Text);
            StringAssert.Contains("- " + availableTool, tail[2].Text);
            StringAssert.Contains("Required tool: '" + availableTool + "'.", tail[2].Text);
            StringAssert.DoesNotContain("- " + unavailableTool, tail[2].Text);
            StringAssert.StartsWith("## World State", tail[3].Text);
            StringAssert.Contains(worldState, tail[3].Text);
        }

        private static AgentMemoryPolicy BuildPolicy()
        {
            AgentMemoryPolicy policy = new();
            policy.SetToolsForRole(RoleId, new ILlmTool[]
            {
                new StubTool(
                    "tool_beta",
                    "{\"type\":\"object\",\"properties\":{\"beta_field\":{\"type\":\"string\"}}}"),
                new StubTool(
                    "tool_alpha",
                    "{\"type\":\"object\",\"properties\":{\"alpha_field\":{\"type\":\"string\"}}}")
            });
            policy.SetRuntimeContextProvider(RoleId, new StudentWorldContextProvider());
            return policy;
        }

        private static AiOrchestrator BuildOrchestrator(
            CapturingLlmClient llm,
            AgentMemoryPolicy policy,
            IAgentMemoryStore memoryStore)
        {
            TestSettings settings = new();
            return new AiOrchestrator(
                new TestAuthority(),
                llm,
                new TestSink(),
                new TestTelemetry(),
                new AiPromptComposer(
                    new StaticSystemPromptProvider(),
                    new NullUserPromptProvider(),
                    null,
                    null,
                    policy,
                    settings),
                memoryStore,
                policy,
                null,
                null,
                settings);
        }

        private sealed class StubTool : ILlmTool
        {
            public StubTool(string name, string schema)
            {
                Name = name;
                ParametersSchema = schema;
            }

            public string Name { get; }
            public string Description => "Stable role tool.";
            public string ParametersSchema { get; }
            public bool AllowDuplicates => false;
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            public LlmCompletionRequest LastRequest { get; private set; }

            public bool SupportsNativeToolCalling { get; set; }

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
                prompt = "Stable teacher role instructions.";
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

        private sealed class StudentWorldContextProvider : IAgentRuntimeContextProvider
        {
            public string BuildContext(AiTaskRequest request, string roleId, string traceId)
            {
                return "current-world=" + request.SourceTag;
            }
        }

        private sealed class TestMemoryStore : IAgentMemoryStore
        {
            private readonly AgentMemoryState _state;
            private readonly ChatMessage[] _history;

            public TestMemoryStore(string memory, params ChatMessage[] history)
            {
                _state = new AgentMemoryState { Memory = memory };
                _history = history ?? Array.Empty<ChatMessage>();
            }

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                state = _state;
                return true;
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
                return _history;
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
            public string UniversalSystemPromptPrefix => "Universal shared rules.";
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
            public bool EnableStreaming => false;
        }
    }
}
