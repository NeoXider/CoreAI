using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Session;
using LLMUnity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Боевой PlayMode тест для ВСЕХ tool calls (memory, execute_lua).
    /// Поддерживает переключение между LLMUnity (локальная GGUF) и OpenAI-compatible HTTP API.
    /// 
    /// Использование:
    /// 1. Локальная LLM: COREAI_PLAYMODE_LLM_BACKEND=llmunity
    /// 2. API (LM Studio): COREAI_PLAYMODE_LLM_BACKEND=http + COREAI_OPENAI_TEST_BASE/MODEL
    /// 3. Auto (по умолчанию): COREAI_PLAYMODE_LLM_BACKEND=auto
    /// </summary>
#if !COREAI_NO_LLM
    public sealed class AllToolCallsPlayModeTests
    {
        private sealed class InMemoryStore : IAgentMemoryStore
        {
            public readonly Dictionary<string, AgentMemoryState> States = new();
            public bool TryLoad(string roleId, out AgentMemoryState state) => States.TryGetValue(roleId, out state);
            public void Save(string roleId, AgentMemoryState state) => States[roleId] = state;
            public void Clear(string roleId) => States.Remove(roleId);
            public void AppendChatMessage(string roleId, string role, string content) { }
            public CoreAI.Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0) => Array.Empty<CoreAI.Ai.ChatMessage>();
        }

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();
            public void Publish(ApplyAiGameCommand command) => Items.Add(command);
        }

        /// <summary>
        /// Тест Memory Tool: сохранение, добавление и очистка памяти.
        /// Переключаемый бэкенд: LLMUnity или HTTP API.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AllToolCalls_MemoryTool_WriteAppendClear()
        {
            Debug.Log("[AllToolCalls] ═══ MEMORY TOOL TEST START ═══");

            // Создаём LLM клиент через фабрику (auto-select backend)
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null, // auto-select из env
                    0.2f,
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log($"[AllToolCalls] Backend: {handle.ResolvedBackend}");

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                // ===== TEST 1: WRITE MEMORY =====
                {
                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    string prompt = "Save this to memory: 'Test craft #1: Iron Sword'\n\n" +
                        "Call the memory tool now:\n" +
                        "```json\n" +
                        "{\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Test craft #1: Iron Sword\"}}\n" +
                        "```";

                    Debug.Log($"[AllToolCalls] TEST 1: Write memory");
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 120f, "memory write");

                    // Проверяем что память сохранена
                    Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState state1),
                        "Memory should be saved after tool call");
                    Debug.Log($"[AllToolCalls] ✓ Memory written: {state1.Memory}");
                }

                // ===== TEST 2: APPEND MEMORY =====
                {
                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    string prompt = "Append this to memory: 'Test craft #2: Steel Shield'\n\n" +
                        "Call the memory tool:\n" +
                        "```json\n" +
                        "{\"name\": \"memory\", \"arguments\": {\"action\": \"append\", \"content\": \"Test craft #2: Steel Shield\"}}\n" +
                        "```";

                    Debug.Log($"[AllToolCalls] TEST 2: Append memory");
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 120f, "memory append");

                    Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState state2),
                        "Memory should exist after append");
                    StringAssert.Contains("Iron Sword", state2.Memory, "Should contain previous craft");
                    StringAssert.Contains("Steel Shield", state2.Memory, "Should contain new craft");
                    Debug.Log($"[AllToolCalls] ✓ Memory appended: {state2.Memory}");
                }

                // ===== TEST 3: CLEAR MEMORY =====
                {
                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    string prompt = "Clear all memory.\n\n" +
                        "Call the memory tool:\n" +
                        "```json\n" +
                        "{\"name\": \"memory\", \"arguments\": {\"action\": \"clear\"}}\n" +
                        "```";

                    Debug.Log($"[AllToolCalls] TEST 3: Clear memory");
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 120f, "memory clear");

                    Assert.IsFalse(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out _),
                        "Memory should be cleared after tool call");
                    Debug.Log($"[AllToolCalls] ✓ Memory cleared");
                }

                Debug.Log("[AllToolCalls] ═══ MEMORY TOOL TEST PASSED ═══");
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        /// Тест Execute Lua Tool: Programmer вызывает Lua код.
        /// Переключаемый бэкенд: LLMUnity или HTTP API.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AllToolCalls_ExecuteLuaTool_Programmer()
        {
            Debug.Log("[AllToolCalls] ═══ EXECUTE LUA TOOL TEST START ═══");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.2f,
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log($"[AllToolCalls] Backend: {handle.ResolvedBackend}");

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                // ===== TEST: EXECUTE LUA =====
                {
                    ListSink sink = new();
                    AiOrchestrator orch = CreateOrchestrator(handle.Client, store, policy, telemetry, composer, sink);

                    string prompt = "Create a simple item using Lua.\n\n" +
                        "Call the execute_lua tool:\n" +
                        "```json\n" +
                        "{\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('TestDagger', 'weapon', 50)\\nreport('crafted TestDagger')\"}}\n" +
                        "```";

                    Debug.Log($"[AllToolCalls] TEST: Execute Lua");
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Programmer,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 120f, "execute_lua");

                    // Проверяем что команда была создана
                    Assert.Greater(sink.Items.Count, 0, "Should produce at least one command");
                    Debug.Log($"[AllToolCalls] ✓ Lua executed, command produced");
                }

                Debug.Log("[AllToolCalls] ═══ EXECUTE LUA TOOL TEST PASSED ═══");
            }
            finally
            {
                handle.Dispose();
            }
        }

        private static AiOrchestrator CreateOrchestrator(
            ILlmClient client,
            IAgentMemoryStore store,
            AgentMemoryPolicy policy,
            SessionTelemetryCollector telemetry,
            AiPromptComposer composer,
            IAiGameCommandSink sink)
        {
            return new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                sink,
                telemetry,
                composer,
                store,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics());
        }
    }
#endif
}
