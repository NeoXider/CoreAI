using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
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

            public bool TryLoad(string roleId, out AgentMemoryState state)
            {
                return States.TryGetValue(roleId, out state);
            }

            public void Save(string roleId, AgentMemoryState state)
            {
                States[roleId] = state;
            }

            public void Clear(string roleId)
            {
                States.Remove(roleId);
            }

            public void AppendChatMessage(string roleId, string role, string content)
            {
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<Ai.ChatMessage>();
            }
        }

        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        /// <summary>
        /// Тест Memory Tool: сохранение, добавление и очистка памяти.
        /// Проверяет РЕАЛЬНЫЙ tool call — модель должна вызвать инструмент, а не ответить текстом.
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
                    0.1f, // низкая температура для надёжного tool calling
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

                // Обернуть клиент с правильным MemoryStore (решает проблему разных store'ов)
                ILlmClient sharedClient = handle.WrapWithMemoryStore(store);

                // Получаем LLMAgent и LLM для keepModelLoaded (только для LLMUnity)
                var llmUnityClient = handle.Client as MeaiLlmUnityClient;
                var agent = llmUnityClient?.UnityAgent;
                var llm = agent?.llm ?? agent?.GetComponent<LLM>();
                if (llm != null)
                {
                    try
                    {
                        var keepProp = llm.GetType().GetProperty("keepModelLoaded");
                        if (keepProp != null)
                        {
                            keepProp.SetValue(llm, true);
                            Debug.Log("[AllToolCalls] keepModelLoaded = true (server stays running)");
                        }
                    }
                    catch { Debug.Log("[AllToolCalls] keepModelLoaded property not found"); }
                }

                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                Debug.Log($"[AllToolCalls] Using client: {sharedClient.GetType().Name}");

                // ===== TEST 1: WRITE MEMORY =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(sharedClient);
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policy, telemetry, composer, sink);

                    // Явный JSON шаблон + инструкция
                    string prompt = "Save this to memory using the memory tool: 'Test craft #1: Iron Sword'\n\n" +
                                    "You MUST call the memory tool with this exact format:\n" +
                                    "{\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Test craft #1: Iron Sword\"}}\n\n" +
                                    "DO NOT respond with text. CALL the memory tool now.";

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

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory write");

                    Debug.Log($"[AllToolCalls] 📥 MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content (FULL):");
                    if (string.IsNullOrEmpty(capturingLlm.LastContent))
                    {
                        Debug.LogWarning("[AllToolCalls] ⚠ Content is EMPTY!");
                    }
                    else
                    {
                        Debug.Log(capturingLlm.LastContent);
                    }
                    Debug.Log($"[AllToolCalls] ─────────────────────────────────────────");

                    // СТРОГАЯ проверка: память должна быть сохранена РЕАЛЬНЫМ tool call'ом
                    bool memorySaved = store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState state1) &&
                                       !string.IsNullOrWhiteSpace(state1.Memory);

                    if (!memorySaved)
                    {
                        Debug.LogError($"[AllToolCalls] ❌ WRITE FAILED: Memory NOT saved by tool call. " +
                            $"Model responded with text instead of calling the memory tool.");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls] ✓ Memory written by tool call: {state1.Memory}");
                    }

                    Assert.IsTrue(memorySaved,
                        "Memory must be saved by actual tool call, not by text response. " +
                        "Model should call the memory tool, not respond with text.");
                }

                // ===== TEST 2: APPEND MEMORY =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(sharedClient);
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policy, telemetry, composer, sink);

                    // Явный JSON шаблон для append
                    string prompt = "Append this to memory using the memory tool: 'Test craft #2: Steel Shield'\n\n" +
                                    "You MUST call the memory tool with this exact format:\n" +
                                    "{\"name\": \"memory\", \"arguments\": {\"action\": \"append\", \"content\": \"Test craft #2: Steel Shield\"}}\n\n" +
                                    "DO NOT respond with text. CALL the memory tool now.";

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

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory append");

                    Debug.Log($"[AllToolCalls] 📥 MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");

                    // СТРОГАЯ проверка: память должна содержать ОБА элемента
                    bool memoryAppended = store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState state2) &&
                                          state2.Memory.Contains("Iron Sword") &&
                                          state2.Memory.Contains("Steel Shield");

                    if (!memoryAppended)
                    {
                        string currentMemory = store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out var s) ? s.Memory : "(none)";
                        Debug.LogError($"[AllToolCalls] ❌ APPEND FAILED: Memory not appended by tool call. " +
                            $"Current memory: '{currentMemory}'. Model responded with text instead.");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls] ✓ Memory appended by tool call: {state2.Memory}");
                    }

                    Assert.IsTrue(memoryAppended,
                        "Memory must be appended by actual tool call. Expected both 'Iron Sword' and 'Steel Shield' in memory.");
                }

                // ===== TEST 3: CLEAR MEMORY =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(sharedClient);
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policy, telemetry, composer, sink);

                    // Явный JSON шаблон для clear
                    string prompt = "Clear all memory using the memory tool.\n\n" +
                                    "You MUST call the memory tool with this exact format:\n" +
                                    "{\"name\": \"memory\", \"arguments\": {\"action\": \"clear\"}}\n\n" +
                                    "DO NOT respond with text. CALL the memory tool now.";

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

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory clear");

                    Debug.Log($"[AllToolCalls] 📥 MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");

                    // СТРОГАЯ проверка: память должна быть удалена РЕАЛЬНЫМ tool call'ом
                    bool memoryCleared = !store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out _);

                    if (!memoryCleared)
                    {
                        string currentMemory = store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out var s) ? s.Memory : "(none)";
                        Debug.LogError($"[AllToolCalls] ❌ CLEAR FAILED: Memory NOT cleared by tool call. " +
                            $"Current memory: '{currentMemory}'. Model responded with text instead.");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls] ✓ Memory cleared by tool call");
                    }

                    Assert.IsTrue(memoryCleared,
                        "Memory must be cleared by actual tool call. Memory store should be empty.");
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
        /// Проверяет РЕАЛЬНЫЙ tool call и что Lua код был валидным и выполнился.
        /// Переключаемый бэкенд: LLMUnity или HTTP API.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AllToolCalls_ExecuteLuaTool_Programmer()
        {
            Debug.Log("[AllToolCalls] ═══ EXECUTE LUA TOOL TEST START ═══");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.1f, // низкая температура для надёжного tool calling
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                Debug.Log($"[AllToolCalls.ExecuteLua] Backend: {handle.ResolvedBackend}");
                Debug.Log($"[AllToolCalls.ExecuteLua] Client: {handle.Client.GetType().Name}");

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                // Обернуть клиент с правильным MemoryStore
                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                // ===== TEST: EXECUTE LUA =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(clientWithMemory);

                    // Добавляем execute_lua tool для Programmer
                    AgentMemoryPolicy policyWithLua = new();
                    policyWithLua.EnableMemoryTool(BuiltInAgentRoleIds.Programmer);

                    AiOrchestrator orch =
                        CreateOrchestrator(capturingLlm, store, policyWithLua, telemetry, composer, sink);

                    // Явный JSON шаблон + инструкция
                    string prompt = "Create a simple item called 'TestDagger' with quality 50.\n\n" +
                                    "You MUST use the execute_lua tool call with this exact format:\n" +
                                    "{\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('TestDagger', 'weapon', 50)\\nreport('crafted TestDagger')\"}}\n\n" +
                                    "DO NOT respond with text. CALL the execute_lua tool now with valid Lua code.";

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

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "execute_lua");

                    Debug.Log($"[AllToolCalls] 📥 MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");
                    Debug.Log($"[AllToolCalls] Commands produced: {sink.Items.Count}");

                    // СТРОГАЯ проверка: команды должны быть опубликованы через sink
                    if (sink.Items.Count == 0)
                    {
                        Debug.LogError($"[AllToolCalls] ❌ EXECUTE LUA FAILED: " +
                            $"No commands produced. Model responded with text instead of calling execute_lua tool. " +
                            $"Response: {capturingLlm.LastContent}");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls] ✓ Lua executed, {sink.Items.Count} command(s) produced");
                        foreach (var cmd in sink.Items)
                        {
                            Debug.Log($"[AllToolCalls]   - Command: {cmd.CommandTypeId}");
                        }
                    }

                    Assert.Greater(sink.Items.Count, 0,
                        "Should produce at least one command. Model must call the execute_lua tool, " +
                        "not respond with text. The Lua code must be valid and produce ApplyAiGameCommand.");
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

        private static async Task<TestResult> RunAgentTestAsync(
            ILlmClient llmClient,
            AgentConfig agentConfig,
            string userMessage,
            IAgentMemoryStore store,
            AgentMemoryPolicy policy,
            SessionTelemetryCollector telemetry,
            AiPromptComposer composer,
            IAiGameCommandSink sink)
        {
            agentConfig.ApplyToPolicy(policy);
            CapturingLlmClient cap = new(llmClient);
            AiOrchestrator orch = CreateOrchestrator(cap, store, policy, telemetry, composer, sink);
            await orch.RunTaskAsync(new AiTaskRequest { RoleId = agentConfig.RoleId, Hint = userMessage });
            return new TestResult { Response = cap.LastContent, ToolsCount = cap.LastTools?.Count ?? 0 };
        }

        private sealed class TestResult
        {
            public string Response { get; set; }
            public int ToolsCount { get; set; }
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
            public IReadOnlyList<ILlmTool> LastTools;

            public CapturingLlmClient(ILlmClient inner)
            {
                _inner = inner;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                System.Threading.CancellationToken cancellationToken = default)
            {
                LastSystemPrompt = request.SystemPrompt;
                LastUserPayload = request.UserPayload;
                LastTools = request.Tools;

                LlmCompletionResult result = await _inner.CompleteAsync(request, cancellationToken);

                if (result != null && result.Ok)
                {
                    LastContent = result.Content;
                }

                return result;
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }
        }
    }
#endif
}