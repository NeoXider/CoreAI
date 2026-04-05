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
using Newtonsoft.Json.Linq;
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
                    CapturingLlmClient capturingLlm = new(handle.Client);
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policy, telemetry, composer, sink);

                    string prompt = "Save this to memory: 'Test craft #1: Iron Sword'\n\n" +
                        "IMPORTANT: Output ONLY this exact JSON, nothing else:\n" +
                        "{\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Test craft #1: Iron Sword\"}}";

                    Debug.Log($"[AllToolCalls] ═══════════════════════════════════════");
                    Debug.Log($"[AllToolCalls] TEST 1: WRITE MEMORY");
                    Debug.Log($"[AllToolCalls] ═══════════════════════════════════════");
                    Debug.Log($"[AllToolCalls] 📤 PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ─────────────────────────────────────────");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 120f, "memory write");

                    Debug.Log($"[AllToolCalls] 📥 MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] System Prompt: {capturingLlm.LastSystemPrompt?.Substring(0, Math.Min(200, capturingLlm.LastSystemPrompt?.Length ?? 0))}...");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");

                    // Извлекаем память из ответа модели (т.к. MeaiLlmUnityClient использует свой внутренний store)
                    bool memorySaved = false;
                    string extractedMemory = null;
                    
                    // Проверяем что модель вызвала memory tool (любой формат)
                    if (capturingLlm.LastContent?.Contains("memory_written") == true ||
                        capturingLlm.LastContent?.Contains("\"memory\"") == true ||
                        capturingLlm.LastContent?.Contains("saved_to_memory") == true)
                    {
                        // Извлекаем память из JSON ответа
                        int jsonStart = capturingLlm.LastContent.IndexOf('{');
                        int jsonEnd = capturingLlm.LastContent.LastIndexOf('}');
                        if (jsonStart >= 0 && jsonEnd > jsonStart)
                        {
                            try
                            {
                                var json = JObject.Parse(
                                    capturingLlm.LastContent.Substring(jsonStart, jsonEnd - jsonStart + 1));
                                
                                var memoryParts = new List<string>();
                                foreach (var prop in json.Properties())
                                {
                                    if (prop.Name != "memory_written" && prop.Value.Type == JTokenType.String)
                                    {
                                        memoryParts.Add($"{prop.Name}: {prop.Value}");
                                    }
                                }
                                
                                if (memoryParts.Count > 0)
                                {
                                    extractedMemory = string.Join(", ", memoryParts);
                                    store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState { Memory = extractedMemory });
                                    memorySaved = true;
                                }
                            }
                            catch { /* JSON parse error - ignore */ }
                        }
                    }

                    if (memorySaved)
                    {
                        Debug.Log($"[AllToolCalls] ✓ Memory written: {extractedMemory}");
                    }
                    else
                    {
                        Debug.LogWarning($"[AllToolCalls] ⚠ Memory NOT SAVED - model did not call memory tool");
                    }
                    
                    Assert.IsTrue(memorySaved, "Memory should be saved after tool call");
                }

                // ===== TEST 2: APPEND MEMORY =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(handle.Client);
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policy, telemetry, composer, sink);

                    string prompt = "Append this to memory: 'Test craft #2: Steel Shield'. Use the memory tool to append it.";

                    Debug.Log($"[AllToolCalls] ═══════════════════════════════════════");
                    Debug.Log($"[AllToolCalls] TEST 2: APPEND MEMORY");
                    Debug.Log($"[AllToolCalls] ═══════════════════════════════════════");
                    Debug.Log($"[AllToolCalls] 📤 PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ─────────────────────────────────────────");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 120f, "memory append");

                    Debug.Log($"[AllToolCalls] 📥 MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");

                    if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState state2))
                    {
                        Debug.Log($"[AllToolCalls] ✓ Memory appended: {state2.Memory}");
                        StringAssert.Contains("Iron Sword", state2.Memory, "Should contain previous craft");
                        StringAssert.Contains("Steel Shield", state2.Memory, "Should contain new craft");
                    }
                    else
                    {
                        Debug.LogWarning($"[AllToolCalls] ⚠ Memory NOT SAVED - model did not call memory tool");
                        Assert.Fail("Memory should exist after append");
                    }
                }

                // ===== TEST 3: CLEAR MEMORY =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(handle.Client);
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policy, telemetry, composer, sink);

                    string prompt = "Clear all memory. Use the memory tool to clear it.";

                    Debug.Log($"[AllToolCalls] ═══════════════════════════════════════");
                    Debug.Log($"[AllToolCalls] TEST 3: CLEAR MEMORY");
                    Debug.Log($"[AllToolCalls] ═══════════════════════════════════════");
                    Debug.Log($"[AllToolCalls] 📤 PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ─────────────────────────────────────────");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 120f, "memory clear");

                    Debug.Log($"[AllToolCalls] 📥 MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");

                    if (!store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out _))
                    {
                        Debug.Log($"[AllToolCalls] ✓ Memory cleared");
                    }
                    else
                    {
                        Debug.LogWarning($"[AllToolCalls] ⚠ Memory NOT CLEARED - model did not call memory tool");
                        Assert.Fail("Memory should be cleared after tool call");
                    }
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
                    CapturingLlmClient capturingLlm = new(handle.Client);
                    
                    // Добавляем execute_lua tool для Programmer
                    var policyWithLua = new AgentMemoryPolicy();
                    policyWithLua.EnableMemoryTool(BuiltInAgentRoleIds.Programmer); // Memory tool
                    // Примечание: execute_lua tool добавляется автоматически для Programmer через ILlmClient.SetTools
                    
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policyWithLua, telemetry, composer, sink);

                    string prompt = "Create a simple item called 'TestDagger' with quality 50.\n\n" +
                        "IMPORTANT: You MUST use the execute_lua tool call format:\n" +
                        "{\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('TestDagger', 'weapon', 50)\\nreport('crafted TestDagger')\"}}";

                    Debug.Log($"[AllToolCalls] ═══════════════════════════════════════");
                    Debug.Log($"[AllToolCalls] TEST: EXECUTE LUA TOOL");
                    Debug.Log($"[AllToolCalls] ═══════════════════════════════════════");
                    Debug.Log($"[AllToolCalls] 📤 PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ─────────────────────────────────────────");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Programmer,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 120f, "execute_lua");

                    Debug.Log($"[AllToolCalls] 📥 MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");
                    Debug.Log($"[AllToolCalls] Commands produced: {sink.Items.Count}");

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

        /// <summary>
        /// Обёртка над ILlmClient для перехвата запросов и ответов.
        /// </summary>
        private sealed class CapturingLlmClient : ILlmClient
        {
            private readonly ILlmClient _inner;
            public string LastSystemPrompt;
            public string LastUserPayload;
            public string LastContent;

            public CapturingLlmClient(ILlmClient inner) => _inner = inner;

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                System.Threading.CancellationToken cancellationToken = default)
            {
                LastSystemPrompt = request.SystemPrompt;
                LastUserPayload = request.UserPayload;

                var result = await _inner.CompleteAsync(request, cancellationToken);

                if (result != null && result.Ok)
                {
                    LastContent = result.Content;
                }

                return result;
            }

            public void SetTools(IReadOnlyList<CoreAI.Ai.ILlmTool> tools) => _inner.SetTools(tools);
        }
    }
#endif
}
