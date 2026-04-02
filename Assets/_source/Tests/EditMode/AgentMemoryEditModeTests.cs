using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class AgentMemoryEditModeTests
    {
        private sealed class CapturingLlm : ILlmClient
        {
            public string LastSystemPrompt;
            public string LastUserPayload;
            private readonly string _reply;

            public CapturingLlm(string reply) => _reply = reply;

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                LastSystemPrompt = request.SystemPrompt;
                LastUserPayload = request.UserPayload;
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _reply });
            }
        }

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();
            public void Publish(ApplyAiGameCommand command) => Items.Add(command);
        }

        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();

            public bool TryLoad(string roleId, out AgentMemoryState state) => States.TryGetValue(roleId, out state);

            public void Save(string roleId, AgentMemoryState state) => States[roleId] = state;

            public void Clear(string roleId) => States.Remove(roleId);
        }

        private sealed class TwoStepMemoryLlm : ILlmClient
        {
            private int _n;
            public string SecondCallSystemPrompt;

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
            {
                _n++;
                if (_n == 1)
                {
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Ok = true,
                        Content = "```memory\nremember: apples\n```"
                    });
                }

                SecondCallSystemPrompt = request.SystemPrompt ?? "";
                var hasMemory = SecondCallSystemPrompt.Contains("remember: apples");
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = hasMemory ? "I remember: apples" : "I have no memory"
                });
            }
        }

        [Test]
        public async Task Creator_WhenMemoryExists_AppendsMemoryToSystemPrompt()
        {
            var llm = new CapturingLlm("{\"ok\":true}");
            var sink = new ListSink();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider());
            var store = new InMemoryStore();
            store.Save(BuiltInAgentRoleIds.Creator, new AgentMemoryState { Memory = "remember_this=1" });
            var orch = new AiOrchestrator(new SoloAuthorityHost(), llm, sink, telemetry, composer, store, new AgentMemoryPolicy());

            await orch.RunTaskAsync(new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Creator, Hint = "x" });

            StringAssert.Contains("## Memory", llm.LastSystemPrompt);
            StringAssert.Contains("remember_this=1", llm.LastSystemPrompt);
        }

        [Test]
        public async Task Analyzer_ByDefaultMemoryDisabled_DoesNotAppendMemoryToSystemPrompt()
        {
            var llm = new CapturingLlm("{\"ok\":true}");
            var sink = new ListSink();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider());
            var store = new InMemoryStore();
            store.Save(BuiltInAgentRoleIds.Analyzer, new AgentMemoryState { Memory = "should_not_be_seen" });
            var orch = new AiOrchestrator(new SoloAuthorityHost(), llm, sink, telemetry, composer, store, new AgentMemoryPolicy());

            await orch.RunTaskAsync(new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Analyzer, Hint = "x" });

            Assert.IsFalse(llm.LastSystemPrompt.Contains("## Memory"));
            Assert.IsFalse(llm.LastSystemPrompt.Contains("should_not_be_seen"));
        }

        [Test]
        public async Task Creator_MemoryDirective_SavesAndIsRemovedFromPublishedEnvelope()
        {
            var reply =
                "hello\n" +
                "```memory\nkey=value\n```\n" +
                "tail";
            var llm = new CapturingLlm(reply);
            var sink = new ListSink();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider());
            var store = new InMemoryStore();
            var orch = new AiOrchestrator(new SoloAuthorityHost(), llm, sink, telemetry, composer, store, new AgentMemoryPolicy());

            await orch.RunTaskAsync(new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Creator, Hint = "x" });

            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out var st));
            Assert.AreEqual("key=value", st.Memory);
            Assert.AreEqual(1, sink.Items.Count);
            StringAssert.DoesNotContain("```memory", sink.Items[0].JsonPayload);
            StringAssert.Contains("hello", sink.Items[0].JsonPayload);
            StringAssert.Contains("tail", sink.Items[0].JsonPayload);
        }

        [Test]
        public async Task Creator_MemoryAppend_AppendsToExisting()
        {
            var reply = "```memory_append\nB\n```";
            var llm = new CapturingLlm(reply);
            var sink = new ListSink();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider());
            var store = new InMemoryStore();
            store.Save(BuiltInAgentRoleIds.Creator, new AgentMemoryState { Memory = "A" });
            var orch = new AiOrchestrator(new SoloAuthorityHost(), llm, sink, telemetry, composer, store, new AgentMemoryPolicy());

            await orch.RunTaskAsync(new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Creator, Hint = "x" });

            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out var st));
            Assert.AreEqual("A\nB", st.Memory);
        }

        [Test]
        public async Task Creator_MemoryClear_ClearsStore()
        {
            var reply = "```memory_clear\n```";
            var llm = new CapturingLlm(reply);
            var sink = new ListSink();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider());
            var store = new InMemoryStore();
            store.Save(BuiltInAgentRoleIds.Creator, new AgentMemoryState { Memory = "A" });
            var orch = new AiOrchestrator(new SoloAuthorityHost(), llm, sink, telemetry, composer, store, new AgentMemoryPolicy());

            await orch.RunTaskAsync(new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Creator, Hint = "x" });

            Assert.IsFalse(store.TryLoad(BuiltInAgentRoleIds.Creator, out _));
        }

        [Test]
        public async Task Creator_WritesMemory_ThenInNewDialog_CanAnswerUsingIt()
        {
            var llm = new TwoStepMemoryLlm();
            var store = new InMemoryStore();
            var telemetry = new SessionTelemetryCollector();
            var composer = new AiPromptComposer(new BuiltInDefaultAgentSystemPromptProvider(), new NoAgentUserPromptTemplateProvider());

            // Диалог 1: модель сама записывает память.
            var sink1 = new ListSink();
            var orch1 = new AiOrchestrator(new SoloAuthorityHost(), llm, sink1, telemetry, composer, store, new AgentMemoryPolicy());
            await orch1.RunTaskAsync(new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Creator, Hint = "store something" });
            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out var st));
            Assert.AreEqual("remember: apples", st.Memory);

            // Диалог 2: новый оркестратор (как новая сессия), но то же хранилище памяти.
            var sink2 = new ListSink();
            var orch2 = new AiOrchestrator(new SoloAuthorityHost(), llm, sink2, telemetry, composer, store, new AgentMemoryPolicy());
            await orch2.RunTaskAsync(new AiTaskRequest { RoleId = BuiltInAgentRoleIds.Creator, Hint = "what do you remember?" });

            StringAssert.Contains("## Memory", llm.SecondCallSystemPrompt);
            StringAssert.Contains("remember: apples", llm.SecondCallSystemPrompt);
            Assert.AreEqual(1, sink2.Items.Count);
            StringAssert.Contains("I remember: apples", sink2.Items[0].JsonPayload);
        }
    }
}

