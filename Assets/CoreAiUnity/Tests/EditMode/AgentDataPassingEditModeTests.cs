using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Integration
{
    /// <summary>
    /// Тесты изоляции памяти между агентами и базового workflow.
    /// Data passing между агентами через общую память — НЕ реализован (TODO).
    /// </summary>
    public sealed class AgentDataPassingEditModeTests
    {
        #region Test Infrastructure

        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();
            public bool TryLoad(string roleId, out AgentMemoryState state) => States.TryGetValue(roleId, out state);
            public void Save(string roleId, AgentMemoryState state) => States[roleId] = state;
            public void Clear(string roleId) => States.Remove(roleId);
            public void AppendChatMessage(string roleId, string role, string content) { }
            public CoreAI.Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) =>
                System.Array.Empty<CoreAI.Ai.ChatMessage>();
        }

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();
            public void Publish(ApplyAiGameCommand command) => Items.Add(command);
        }

        private sealed class CapturingLlmClient : ILlmClient
        {
            public readonly List<LlmCompletionRequest> Requests = new();
            public Func<LlmCompletionRequest, LlmCompletionResult> ResponseFactory;

            public Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                System.Threading.CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                if (ResponseFactory != null)
                {
                    return Task.FromResult(ResponseFactory(request));
                }
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = "ok" });
            }
        }

        private static AiOrchestrator CreateOrchestrator(
            ILlmClient client,
            IAgentMemoryStore store,
            IAiGameCommandSink sink)
        {
            return new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                sink,
                new SessionTelemetryCollector(),
                new AiPromptComposer(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore()),
                store,
                new AgentMemoryPolicy(),
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics());
        }

        #endregion

        #region Test 1: Memory Isolation Between Agents

        [Test]
        public async Task DataPassing_DifferentAgents_IsolatedMemory()
        {
            InMemoryStore store = new();
            CapturingLlmClient llm = new();
            ListSink creatorSink = new();
            ListSink mechanicSink = new();

            llm.ResponseFactory = req =>
            {
                if (req.AgentRoleId == BuiltInAgentRoleIds.Creator)
                {
                    return new LlmCompletionResult
                    {
                        Ok = true,
                        Content = "```memory\nCreator design data\n```"
                    };
                }
                if (req.AgentRoleId == BuiltInAgentRoleIds.CoreMechanic)
                {
                    return new LlmCompletionResult
                    {
                        Ok = true,
                        Content = "```memory\nMechanic calculation data\n```"
                    };
                }
                return new LlmCompletionResult { Ok = true, Content = "ok" };
            };

            // Creator пишет своё
            AiOrchestrator creatorOrch = CreateOrchestrator(llm, store, creatorSink);
            await creatorOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Save design data"
            });

            // CoreMechanicAI пишет своё
            AiOrchestrator mechanicOrch = CreateOrchestrator(llm, store, mechanicSink);
            await mechanicOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.CoreMechanic,
                Hint = "Save calculation data"
            });

            // Проверяем изоляцию — КАЖДЫЙ агент имеет СВОЮ память
            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out var creatorMem));
            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out var mechanicMem));

            Assert.AreEqual("Creator design data", creatorMem.Memory);
            Assert.AreEqual("Mechanic calculation data", mechanicMem.Memory);
            Assert.AreNotEqual(creatorMem.Memory, mechanicMem.Memory,
                "Creator and CoreMechanicAI must have isolated memory");
        }

        #endregion

        #region Test 3: Memory Persists Across Multiple Calls

        [Test]
        public async Task DataPassing_MultipleCalls_MemoryAccumulates()
        {
            InMemoryStore store = new();
            CapturingLlmClient llm = new();
            ListSink sink = new();

            int callCount = 0;
            llm.ResponseFactory = req =>
            {
                callCount++;
                return new LlmCompletionResult
                {
                    Ok = true,
                    Content = $"```memory\nCraft #{callCount}: Item {callCount}\n```"
                };
            };

            // Call 1
            AiOrchestrator orch1 = CreateOrchestrator(llm, store, sink);
            await orch1.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.CoreMechanic,
                Hint = "Craft item 1"
            });

            // Call 2 — перезаписывает память
            AiOrchestrator orch2 = CreateOrchestrator(llm, store, sink);
            await orch2.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.CoreMechanic,
                Hint = "Craft item 2"
            });

            // Память должна быть от последнего вызова (write, не append)
            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out var mem));
            StringAssert.Contains("Craft #2", mem.Memory);
        }

        #endregion

        #region Test 4: Data Can Be Passed Via Hint

        [Test]
        public async Task DataPassing_ViaHint_DataAvailableInSystemPrompt()
        {
            InMemoryStore store = new();
            CapturingLlmClient llm = new();
            ListSink sink = new();

            llm.ResponseFactory = req => new LlmCompletionResult { Ok = true, Content = "ok" };

            AiOrchestrator orch = CreateOrchestrator(llm, store, sink);
            await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Create Lua for: Iron Fireblade (damage=45, fire=15)"
            });

            // Hint попадает в user payload
            string userPayload = llm.Requests[0].UserPayload;
            StringAssert.Contains("Iron Fireblade", userPayload,
                "Data passed via Hint should be visible in user payload");
        }

        #endregion
    }
}
