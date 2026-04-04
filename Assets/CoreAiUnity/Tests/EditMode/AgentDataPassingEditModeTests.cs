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
    /// Тесты передачи данных между агентами:
    /// выход Creator → вход CoreMechanicAI → выход Programmer.
    /// Проверяют что результат одного агента используется следующим.
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

        #region Test 1: Creator Output Visible to CoreMechanic

        [Test]
        public async Task DataPassing_CreatorOutput_AvailableInCoreMechanicMemory()
        {
            InMemoryStore store = new();
            CapturingLlmClient llm = new();
            ListSink creatorSink = new();
            ListSink mechanicSink = new();

            // Creator отвечает дизайном
            llm.ResponseFactory = req =>
            {
                if (req.AgentRoleId == BuiltInAgentRoleIds.Creator)
                {
                    return new LlmCompletionResult
                    {
                        Ok = true,
                        Content = "{\"design\": \"Iron+Fire Crystal weapon\"}\n" +
                                  "```memory\nDesign: Iron+Fire Crystal → weapon, damage ~45\n```"
                    };
                }
                return new LlmCompletionResult { Ok = true, Content = "ok" };
            };

            // ===== ШАГ 1: Creator проектирует =====
            AiOrchestrator creatorOrch = CreateOrchestrator(llm, store, creatorSink);
            await creatorOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Design a weapon from Iron + Fire Crystal"
            });

            // Проверяем что Creator записал в память
            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out var creatorMem));
            Assert.IsTrue(creatorMem.Memory.Contains("Iron+Fire Crystal"));

            // ===== ШАГ 2: CoreMechanicAI видит дизайн Creator =====
            llm.ResponseFactory = req =>
            {
                if (req.AgentRoleId == BuiltInAgentRoleIds.CoreMechanic)
                {
                    // Проверяем что в system prompt есть память от Creator
                    bool hasCreatorMemory = req.SystemPrompt.Contains("Iron+Fire Crystal");
                    return new LlmCompletionResult
                    {
                        Ok = true,
                        Content = hasCreatorMemory
                            ? "{\"item\": \"Iron Fireblade\", \"damage\": 45}"
                            : "{\"item\": \"unknown\"}"
                    };
                }
                return new LlmCompletionResult { Ok = true, Content = "ok" };
            };

            AiOrchestrator mechanicOrch = CreateOrchestrator(llm, store, mechanicSink);
            await mechanicOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.CoreMechanic,
                Hint = "Calculate weapon stats (use Creator design from memory)"
            });

            // Проверяем что CoreMechanicAI ВИДЕЛ память Creator
            Assert.AreEqual(2, llm.Requests.Count);
            string mechanicSystemPrompt = llm.Requests[1].SystemPrompt;
            StringAssert.Contains("Iron+Fire Crystal", mechanicSystemPrompt,
                "CoreMechanicAI should see Creator's memory in system prompt");
        }

        #endregion

        #region Test 2: CoreMechanic Output Visible to Programmer

        [Test]
        public async Task DataPassing_CoreMechanicOutput_AvailableInProgrammerMemory()
        {
            InMemoryStore store = new();
            CapturingLlmClient llm = new();
            ListSink mechanicSink = new();
            ListSink programmerSink = new();

            // CoreMechanicAI отвечает расчётом
            llm.ResponseFactory = req =>
            {
                if (req.AgentRoleId == BuiltInAgentRoleIds.CoreMechanic)
                {
                    return new LlmCompletionResult
                    {
                        Ok = true,
                        Content = "{\"damage\": 45, \"fire_damage\": 15}\n" +
                                  "```memory\nCraft#1: Iron Fireblade damage:45 fire:15\n```"
                    };
                }
                return new LlmCompletionResult { Ok = true, Content = "ok" };
            };

            // ===== ШАГ 1: CoreMechanicAI считает =====
            AiOrchestrator mechanicOrch = CreateOrchestrator(llm, store, mechanicSink);
            await mechanicOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.CoreMechanic,
                Hint = "Calculate Iron Fireblade stats"
            });

            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out var mechanicMem));
            Assert.IsTrue(mechanicMem.Memory.Contains("Iron Fireblade"));
            Assert.IsTrue(mechanicMem.Memory.Contains("damage:45"));

            // ===== ШАГ 2: Programmer видит расчёт CoreMechanicAI =====
            llm.ResponseFactory = req =>
            {
                if (req.AgentRoleId == BuiltInAgentRoleIds.Programmer)
                {
                    bool hasMechanicMemory = req.SystemPrompt.Contains("damage:45");
                    return new LlmCompletionResult
                    {
                        Ok = true,
                        Content = hasMechanicMemory
                            ? "```lua\ncreate_item('Iron Fireblade', 'weapon', 75)\n```"
                            : "```lua\ncreate_item('unknown', 'weapon', 0)\n```"
                    };
                }
                return new LlmCompletionResult { Ok = true, Content = "ok" };
            };

            AiOrchestrator programmerOrch = CreateOrchestrator(llm, store, programmerSink);
            await programmerOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Generate Lua for Iron Fireblade (use CoreMechanicAI stats from memory)"
            });

            // Проверяем что Programmer ВИДЕЛ память CoreMechanicAI
            string programmerSystemPrompt = llm.Requests[1].SystemPrompt;
            StringAssert.Contains("damage:45", programmerSystemPrompt,
                "Programmer should see CoreMechanicAI's memory in system prompt");

            // Проверяем что Programmer создал правильный Lua
            Assert.AreEqual(1, programmerSink.Items.Count);
            StringAssert.Contains("Iron Fireblade", programmerSink.Items[0].JsonPayload);
        }

        #endregion

        #region Test 3: Memory Isolation Between Agents

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
                        Content = "{\"tool\": \"memory\", \"action\": \"write\", " +
                                  "\"content\": \"Creator design data\"}"
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

            // Проверяем изоляцию
            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out var creatorMem));
            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out var mechanicMem));

            Assert.AreEqual("Creator design data", creatorMem.Memory);
            Assert.AreEqual("Mechanic calculation data", mechanicMem.Memory);
            Assert.AreNotEqual(creatorMem.Memory, mechanicMem.Memory,
                "Creator and CoreMechanicAI must have isolated memory");
        }

        #endregion

        #region Test 4: Full Chain Creator → CoreMechanic → Programmer

        [Test]
        public async Task DataPassing_FullChain_AllAgentsSeePreviousOutput()
        {
            InMemoryStore store = new();
            CapturingLlmClient llm = new();
            ListSink creatorSink = new();
            ListSink mechanicSink = new();
            ListSink programmerSink = new();

            int step = 0;
            llm.ResponseFactory = req =>
            {
                step++;
                if (req.AgentRoleId == BuiltInAgentRoleIds.Creator)
                {
                    return new LlmCompletionResult
                    {
                        Ok = true,
                        Content = "{\"design\": \"weapon\"}\n" +
                                  "{\"tool\": \"memory\", \"action\": \"write\", " +
                                  "\"content\": \"Design: Steel Sword\"}"
                    };
                }
                if (req.AgentRoleId == BuiltInAgentRoleIds.CoreMechanic)
                {
                    bool sawCreator = req.SystemPrompt.Contains("Steel Sword");
                    return new LlmCompletionResult
                    {
                        Ok = true,
                        Content = sawCreator
                            ? "{\"damage\": 50}\n```memory\nCraft#1: Steel Sword damage:50\n```"
                            : "{\"damage\": 0}"
                    };
                }
                if (req.AgentRoleId == BuiltInAgentRoleIds.Programmer)
                {
                    bool sawMechanic = req.SystemPrompt.Contains("damage:50");
                    return new LlmCompletionResult
                    {
                        Ok = true,
                        Content = sawMechanic
                            ? "```lua\ncreate_item('Steel Sword', 'weapon', 80)\n```"
                            : "```lua\ncreate_item('unknown', 'weapon', 0)\n```"
                    };
                }
                return new LlmCompletionResult { Ok = true, Content = "ok" };
            };

            // Creator
            AiOrchestrator creatorOrch = CreateOrchestrator(llm, store, creatorSink);
            await creatorOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Design Steel Sword"
            });

            // CoreMechanicAI
            AiOrchestrator mechanicOrch = CreateOrchestrator(llm, store, mechanicSink);
            await mechanicOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.CoreMechanic,
                Hint = "Calculate Steel Sword stats"
            });

            // Programmer
            AiOrchestrator programmerOrch = CreateOrchestrator(llm, store, programmerSink);
            await programmerOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Generate Lua for Steel Sword"
            });

            // ===== ФИНАЛЬНАЯ ПРОВЕРКА =====
            Assert.AreEqual(3, llm.Requests.Count, "3 agents should have been called");

            // Creator memory
            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out var creatorMem));
            StringAssert.Contains("Steel Sword", creatorMem.Memory);

            // CoreMechanicAI memory
            Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out var mechanicMem));
            StringAssert.Contains("damage:50", mechanicMem.Memory);

            // Programmer output
            Assert.AreEqual(1, programmerSink.Items.Count);
            StringAssert.Contains("Steel Sword", programmerSink.Items[0].JsonPayload);

            // System prompts chain
            StringAssert.Contains("Steel Sword", llm.Requests[1].SystemPrompt,
                "CoreMechanicAI should see Creator output");
            StringAssert.Contains("damage:50", llm.Requests[2].SystemPrompt,
                "Programmer should see CoreMechanicAI output");
        }

        #endregion
    }
}
