using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
    /// Р‘РѕРµРІРѕР№ PlayMode С‚РµСЃС‚ РґР»СЏ Р’РЎР•РҐ tool calls (memory, execute_lua).
    /// РџРѕРґРґРµСЂР¶РёРІР°РµС‚ РїРµСЂРµРєР»СЋС‡РµРЅРёРµ РјРµР¶РґСѓ LLMUnity (Р»РѕРєР°Р»СЊРЅР°СЏ GGUF) Рё OpenAI-compatible HTTP API.
    /// 
    /// РСЃРїРѕР»СЊР·РѕРІР°РЅРёРµ:
    /// 1. Р›РѕРєР°Р»СЊРЅР°СЏ LLM: COREAI_PLAYMODE_LLM_BACKEND=llmunity
    /// 2. API (LM Studio): COREAI_PLAYMODE_LLM_BACKEND=http + COREAI_OPENAI_TEST_BASE/MODEL
    /// 3. Auto (РїРѕ СѓРјРѕР»С‡Р°РЅРёСЋ): COREAI_PLAYMODE_LLM_BACKEND=auto
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
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

            public void ClearChatHistory(string roleId)
            {
            }

            public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
            {
            }

            public Ai.ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<Ai.ChatMessage>();
            }
        }

        private sealed class TestLuaExecutor : LuaTool.ILuaExecutor
        {
            private readonly IAiGameCommandSink _sink;
            private readonly Sandbox.SecureLuaEnvironment _sandbox;
            private readonly Sandbox.LuaApiRegistry _registry;

            public TestLuaExecutor(IAiGameCommandSink sink)
            {
                _sink = sink;
                _sandbox = new Sandbox.SecureLuaEnvironment();
                _registry = new Sandbox.LuaApiRegistry();
                _registry.Register("report", new Action<string>(msg =>
                {
                    _sink.Publish(new ApplyAiGameCommand
                    {
                        CommandTypeId = AiGameCommandTypeIds.Envelope,
                        JsonPayload = "{\"action\":\"report\", \"message\":\"" + msg + "\"}"
                    });
                    Debug.Log($"[Lua.report] {msg}");
                }));
                _registry.Register("create_item", new Action<string, string, double>((name, type, quality) =>
                {
                    _sink.Publish(new ApplyAiGameCommand
                    {
                        CommandTypeId = AiGameCommandTypeIds.Envelope,
                        JsonPayload = "{\"action\":\"create_item\", \"name\":\"" + name + "\"}"
                    });
                    Debug.Log($"[Lua.create_item] name={name}, type={type}, quality={quality}");
                }));
            }

            public Task<LuaTool.LuaResult> ExecuteAsync(string code, System.Threading.CancellationToken ct)
            {
                try
                {
                    MoonSharp.Interpreter.Script script = _sandbox.CreateScript(_registry);
                    MoonSharp.Interpreter.DynValue result = _sandbox.RunChunk(script, code);
                    return Task.FromResult(
                        new LuaTool.LuaResult { Success = true, Output = result?.ToString() ?? "ok" });
                }
                catch (Exception ex)
                {
                    return Task.FromResult(new LuaTool.LuaResult { Success = false, Error = ex.Message });
                }
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
        /// РўРµСЃС‚ Memory Tool: СЃРѕС…СЂР°РЅРµРЅРёРµ, РґРѕР±Р°РІР»РµРЅРёРµ Рё РѕС‡РёСЃС‚РєР° РїР°РјСЏС‚Рё.
        /// РџСЂРѕРІРµСЂСЏРµС‚ Р Р•РђР›Р¬РќР«Р™ tool call вЂ” РјРѕРґРµР»СЊ РґРѕР»Р¶РЅР° РІС‹Р·РІР°С‚СЊ РёРЅСЃС‚СЂСѓРјРµРЅС‚, Р° РЅРµ РѕС‚РІРµС‚РёС‚СЊ С‚РµРєСЃС‚РѕРј.
        /// РџРµСЂРµРєР»СЋС‡Р°РµРјС‹Р№ Р±СЌРєРµРЅРґ: LLMUnity РёР»Рё HTTP API.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AllToolCalls_MemoryTool_WriteAppendClear()
        {
            Debug.Log("[AllToolCalls] в•ђв•ђв•ђ MEMORY TOOL TEST START в•ђв•ђв•ђ");

            // РЎРѕР·РґР°С‘Рј LLM РєР»РёРµРЅС‚ С‡РµСЂРµР· С„Р°Р±СЂРёРєСѓ (auto-select backend)
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null, // auto-select РёР· env
                    0.1f, // РЅРёР·РєР°СЏ С‚РµРјРїРµСЂР°С‚СѓСЂР° РґР»СЏ РЅР°РґС‘Р¶РЅРѕРіРѕ tool calling
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                Debug.Log($"[AllToolCalls] Backend: {handle.ResolvedBackend}");

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();

                // РћР±РµСЂРЅСѓС‚СЊ РєР»РёРµРЅС‚ СЃ РїСЂР°РІРёР»СЊРЅС‹Рј MemoryStore (СЂРµС€Р°РµС‚ РїСЂРѕР±Р»РµРјСѓ СЂР°Р·РЅС‹С… store'РѕРІ)
                ILlmClient sharedClient = handle.WrapWithMemoryStore(store);

                // РџРѕР»СѓС‡Р°РµРј LLMAgent Рё LLM РґР»СЏ keepModelLoaded (С‚РѕР»СЊРєРѕ РґР»СЏ LLMUnity)
                MeaiLlmUnityClient llmUnityClient = handle.Client as MeaiLlmUnityClient;
                LLMAgent agent = llmUnityClient?.UnityAgent;
                LLM llm = agent?.llm ?? agent?.GetComponent<LLM>();
                if (llm != null)
                {
                    try
                    {
                        PropertyInfo keepProp = llm.GetType().GetProperty("keepModelLoaded");
                        if (keepProp != null)
                        {
                            keepProp.SetValue(llm, true);
                            Debug.Log("[AllToolCalls] keepModelLoaded = true (server stays running)");
                        }
                    }
                    catch
                    {
                        Debug.Log("[AllToolCalls] keepModelLoaded property not found");
                    }
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

                    // Р•СЃС‚РµСЃС‚РІРµРЅРЅС‹Р№ Р·Р°РїСЂРѕСЃ РґР»СЏ native tool calling
                    string prompt = "Save this to memory using the memory tool: 'Test craft #1: Iron Sword'. CALL the memory tool now.";

                    Debug.Log($"[AllToolCalls] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                    Debug.Log($"[AllToolCalls] TEST 1: WRITE MEMORY");
                    Debug.Log($"[AllToolCalls] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                    Debug.Log($"[AllToolCalls] рџ“¤ PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory write");

                    Debug.Log($"[AllToolCalls] рџ“Ґ MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content (FULL):");
                    if (string.IsNullOrEmpty(capturingLlm.LastContent))
                    {
                        Debug.LogWarning("[AllToolCalls] вљ  Content is EMPTY!");
                    }
                    else
                    {
                        Debug.Log(capturingLlm.LastContent);
                    }

                    Debug.Log($"[AllToolCalls] в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

                    // РЎРўР РћР“РђРЇ РїСЂРѕРІРµСЂРєР°: РїР°РјСЏС‚СЊ РґРѕР»Р¶РЅР° Р±С‹С‚СЊ СЃРѕС…СЂР°РЅРµРЅР° Р Р•РђР›Р¬РќР«Рњ tool call'РѕРј
                    bool memorySaved = store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState state1) &&
                                       !string.IsNullOrWhiteSpace(state1.Memory);

                    if (!memorySaved)
                    {
                        Debug.LogWarning($"[AllToolCalls] вќЊ WRITE FAILED: Memory NOT saved by tool call. " +
                                       $"Model responded with text instead of calling the memory tool.");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls] вњ“ Memory written by tool call: {state1.Memory}");
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

                    // Р•СЃС‚РµСЃС‚РІРµРЅРЅС‹Р№ Р·Р°РїСЂРѕСЃ РґР»СЏ native tool calling
                    string prompt = "Append this to memory using the memory tool: 'Test craft #2: Steel Shield'. CALL the memory tool now.";

                    Debug.Log($"[AllToolCalls] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                    Debug.Log($"[AllToolCalls] TEST 2: APPEND MEMORY");
                    Debug.Log($"[AllToolCalls] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                    Debug.Log($"[AllToolCalls] рџ“¤ PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory append");

                    Debug.Log($"[AllToolCalls] рџ“Ґ MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");

                    // РЎРўР РћР“РђРЇ РїСЂРѕРІРµСЂРєР°: РїР°РјСЏС‚СЊ РґРѕР»Р¶РЅР° СЃРѕРґРµСЂР¶Р°С‚СЊ РћР‘Рђ СЌР»РµРјРµРЅС‚Р°
                    bool memoryAppended =
                        store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState state2) &&
                        state2.Memory.Contains("Iron Sword") &&
                        state2.Memory.Contains("Steel Shield");

                    if (!memoryAppended)
                    {
                        string currentMemory = store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState s)
                            ? s.Memory
                            : "(none)";
                        Debug.LogWarning($"[AllToolCalls] вќЊ APPEND FAILED: Memory not appended by tool call. " +
                                       $"Current memory: '{currentMemory}'. Model responded with text instead.");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls] вњ“ Memory appended by tool call: {state2.Memory}");
                    }

                    Assert.IsTrue(memoryAppended,
                        "Memory must be appended by actual tool call. Expected both 'Iron Sword' and 'Steel Shield' in memory.");
                }

                // ===== TEST 3: CLEAR MEMORY =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(sharedClient);
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policy, telemetry, composer, sink);

                    // Р•СЃС‚РµСЃС‚РІРµРЅРЅС‹Р№ Р·Р°РїСЂРѕСЃ РґР»СЏ native tool calling
                    string prompt = "Clear all memory using the memory tool. CALL the memory tool now.";

                    Debug.Log($"[AllToolCalls] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                    Debug.Log($"[AllToolCalls] TEST 3: CLEAR MEMORY");
                    Debug.Log($"[AllToolCalls] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                    Debug.Log($"[AllToolCalls] рџ“¤ PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory clear");

                    Debug.Log($"[AllToolCalls] рџ“Ґ MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");

                    // РЎРўР РћР“РђРЇ РїСЂРѕРІРµСЂРєР°: РїР°РјСЏС‚СЊ РґРѕР»Р¶РЅР° Р±С‹С‚СЊ СѓРґР°Р»РµРЅР° Р Р•РђР›Р¬РќР«Рњ tool call'РѕРј
                    bool memoryCleared = !store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out _);

                    if (!memoryCleared)
                    {
                        string currentMemory = store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState s)
                            ? s.Memory
                            : "(none)";
                        Debug.LogWarning($"[AllToolCalls] вќЊ CLEAR FAILED: Memory NOT cleared by tool call. " +
                                       $"Current memory: '{currentMemory}'. Model responded with text instead.");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls] вњ“ Memory cleared by tool call");
                    }

                    Assert.IsTrue(memoryCleared,
                        "Memory must be cleared by actual tool call. Memory store should be empty.");
                }

                Debug.Log("[AllToolCalls] в•ђв•ђв•ђ MEMORY TOOL TEST PASSED в•ђв•ђв•ђ");
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        /// РўРµСЃС‚ Execute Lua Tool: Programmer РІС‹Р·С‹РІР°РµС‚ Lua РєРѕРґ.
        /// РџСЂРѕРІРµСЂСЏРµС‚ Р Р•РђР›Р¬РќР«Р™ tool call Рё С‡С‚Рѕ Lua РєРѕРґ Р±С‹Р» РІР°Р»РёРґРЅС‹Рј Рё РІС‹РїРѕР»РЅРёР»СЃСЏ.
        /// РџРµСЂРµРєР»СЋС‡Р°РµРјС‹Р№ Р±СЌРєРµРЅРґ: LLMUnity РёР»Рё HTTP API.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AllToolCalls_ExecuteLuaTool_Programmer()
        {
            Debug.Log("[AllToolCalls] в•ђв•ђв•ђ EXECUTE LUA TOOL TEST START в•ђв•ђв•ђ");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.1f, // РЅРёР·РєР°СЏ С‚РµРјРїРµСЂР°С‚СѓСЂР° РґР»СЏ РЅР°РґС‘Р¶РЅРѕРіРѕ tool calling
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                Debug.Log($"[AllToolCalls.ExecuteLua] Backend: {handle.ResolvedBackend}");
                Debug.Log($"[AllToolCalls.ExecuteLua] Client: {handle.Client.GetType().Name}");

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                // РћР±РµСЂРЅСѓС‚СЊ РєР»РёРµРЅС‚ СЃ РїСЂР°РІРёР»СЊРЅС‹Рј MemoryStore
                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                // ===== TEST: EXECUTE LUA =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(clientWithMemory);

                    // Р—Р°РіР»СѓС€РєР° РЅР°СЃС‚СЂРѕРµРє
                    CoreAISettingsAsset tempSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                    
                    // Р”РѕР±Р°РІР»СЏРµРј execute_lua tool РґР»СЏ Programmer
                    AgentMemoryPolicy policyWithLua = new();
                    policyWithLua.EnableMemoryTool(BuiltInAgentRoleIds.Programmer);
                    policyWithLua.SetToolsForRole(BuiltInAgentRoleIds.Programmer,
                        new ILlmTool[] { new LuaLlmTool(new TestLuaExecutor(sink), tempSettings, CoreAI.Logging.NullLog.Instance) });

                    AiOrchestrator orch =
                        CreateOrchestrator(capturingLlm, store, policyWithLua, telemetry, composer, sink);

                    // Р•СЃС‚РµСЃС‚РІРµРЅРЅС‹Р№ Р·Р°РїСЂРѕСЃ РґР»СЏ native tool calling
                    string prompt = "Create a simple item called 'TestDagger' with quality 50. You MUST use the execute_lua tool call.";

                    Debug.Log($"[AllToolCalls] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                    Debug.Log($"[AllToolCalls] TEST: EXECUTE LUA TOOL");
                    Debug.Log($"[AllToolCalls] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                    Debug.Log($"[AllToolCalls] рџ“¤ PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Programmer,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "execute_lua");

                    Debug.Log($"[AllToolCalls] рџ“Ґ MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");
                    Debug.Log($"[AllToolCalls] Commands produced: {sink.Items.Count}");

                    // РЎРўР РћР“РђРЇ РїСЂРѕРІРµСЂРєР°: РєРѕРјР°РЅРґС‹ РґРѕР»Р¶РЅС‹ Р±С‹С‚СЊ РѕРїСѓР±Р»РёРєРѕРІР°РЅС‹ С‡РµСЂРµР· sink
                    if (sink.Items.Count == 0)
                    {
                        Debug.LogWarning($"[AllToolCalls] вќЊ EXECUTE LUA FAILED: " +
                                       $"No commands produced. Model responded with text instead of calling execute_lua tool. " +
                                       $"Response: {capturingLlm.LastContent}");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls] вњ“ Lua executed, {sink.Items.Count} command(s) produced");
                        foreach (ApplyAiGameCommand cmd in sink.Items)
                        {
                            Debug.Log($"[AllToolCalls]   - Command: {cmd.CommandTypeId}");
                        }
                    }

                    Assert.Greater(sink.Items.Count, 0,
                        "Should produce at least one command. Model must call the execute_lua tool, " +
                        "not respond with text. The Lua code must be valid and produce ApplyAiGameCommand.");
                }

                Debug.Log("[AllToolCalls] в•ђв•ђв•ђ EXECUTE LUA TOOL TEST PASSED в•ђв•ђв•ђ");
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
                new NullAiOrchestrationMetrics(), UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());
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
        /// РћР±С‘СЂС‚РєР° РЅР°Рґ ILlmClient РґР»СЏ РїРµСЂРµС…РІР°С‚Р° Р·Р°РїСЂРѕСЃРѕРІ Рё РѕС‚РІРµС‚РѕРІ.
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
