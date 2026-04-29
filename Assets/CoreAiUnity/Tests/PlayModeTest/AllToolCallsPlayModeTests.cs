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
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
#endif
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    ///  PlayMode    tool calls (memory, execute_lua).
    ///    LLMUnity ( GGUF)  OpenAI-compatible HTTP API.
    /// 
    /// :
    /// 1.  LLM: COREAI_PLAYMODE_LLM_BACKEND=llmunity
    /// 2. API (LM Studio): COREAI_PLAYMODE_LLM_BACKEND=http + COREAI_OPENAI_TEST_BASE/MODEL
    /// 3. Auto ( ): COREAI_PLAYMODE_LLM_BACKEND=auto
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
        ///  Memory Tool: ,    .
        ///   tool call     ,    .
        ///  : LLMUnity  HTTP API.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AllToolCalls_MemoryTool_WriteAppendClear()
        {
            Debug.Log("[AllToolCalls]  MEMORY TOOL TEST START ");

            //  LLM    (auto-select backend)
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null, // auto-select  env
                    0.1f, //     tool calling
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
                TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.CoreMechanic);

                //     MemoryStore (   store')
                ILlmClient sharedClient = handle.WrapWithMemoryStore(store);

                //  LLMAgent  LLM  keepModelLoaded (  LLMUnity)
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
                MeaiLlmUnityClient llmUnityClient = handle.Client as MeaiLlmUnityClient;
                LLMAgent agent = llmUnityClient?.UnityAgent;
                LLM llm = agent?.llm ?? agent?.GetComponent<LLM>();
#else
                object llmUnityClient = null;
                object llm = null;
#endif
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

                    //    native tool calling
                    string prompt = "Save this to memory using the memory tool: 'Test craft #1: Iron Sword'. CALL the memory tool now.";

                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls] TEST 1: WRITE MEMORY");
                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls]  PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory write");

                    Debug.Log($"[AllToolCalls]  MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content (FULL):");
                    if (string.IsNullOrEmpty(capturingLlm.LastContent))
                    {
                        Debug.LogWarning("[AllToolCalls]  Content is EMPTY!");
                    }
                    else
                    {
                        Debug.Log(capturingLlm.LastContent);
                    }

                    Debug.Log($"[AllToolCalls] ");

                    //  :      tool call'
                    bool memorySaved = store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState state1) &&
                                       !string.IsNullOrWhiteSpace(state1.Memory);

                    if (!memorySaved)
                    {
                        Debug.LogWarning($"[AllToolCalls]  WRITE FAILED: Memory NOT saved by tool call. " +
                                       $"Model responded with text instead of calling the memory tool.");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls]  Memory written by tool call: {state1.Memory}");
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

                    //    native tool calling
                    string prompt = "Append this to memory using the memory tool: 'Test craft #2: Steel Shield'. CALL the memory tool now.";

                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls] TEST 2: APPEND MEMORY");
                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls]  PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory append");

                    Debug.Log($"[AllToolCalls]  MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");

                    //  :     
                    bool memoryAppended =
                        store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState state2) &&
                        state2.Memory.Contains("Iron Sword") &&
                        state2.Memory.Contains("Steel Shield");

                    if (!memoryAppended)
                    {
                        string currentMemory = store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState s)
                            ? s.Memory
                            : "(none)";
                        Debug.LogWarning($"[AllToolCalls]  APPEND FAILED: Memory not appended by tool call. " +
                                       $"Current memory: '{currentMemory}'. Model responded with text instead.");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls]  Memory appended by tool call: {state2.Memory}");
                    }

                    Assert.IsTrue(memoryAppended,
                        "Memory must be appended by actual tool call. Expected both 'Iron Sword' and 'Steel Shield' in memory.");
                }

                // ===== TEST 3: CLEAR MEMORY =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(sharedClient);
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policy, telemetry, composer, sink);

                    //    native tool calling
                    string prompt = "Clear all memory using the memory tool. CALL the memory tool now.";

                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls] TEST 3: CLEAR MEMORY");
                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls]  PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory clear");

                    Debug.Log($"[AllToolCalls]  MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");

                    //  :      tool call'
                    bool memoryCleared = !store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out _);

                    if (!memoryCleared)
                    {
                        string currentMemory = store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState s)
                            ? s.Memory
                            : "(none)";
                        Debug.LogWarning($"[AllToolCalls]  CLEAR FAILED: Memory NOT cleared by tool call. " +
                                       $"Current memory: '{currentMemory}'. Model responded with text instead.");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls]  Memory cleared by tool call");
                    }

                    Assert.IsTrue(memoryCleared,
                        "Memory must be cleared by actual tool call. Memory store should be empty.");
                }

                Debug.Log("[AllToolCalls]  MEMORY TOOL TEST PASSED ");
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        ///  Execute Lua Tool: Programmer  Lua .
        ///   tool call   Lua     .
        ///  : LLMUnity  HTTP API.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator AllToolCalls_ExecuteLuaTool_Programmer()
        {
            Debug.Log("[AllToolCalls]  EXECUTE LUA TOOL TEST START ");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.1f, //     tool calling
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
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                //     MemoryStore
                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                // ===== TEST: EXECUTE LUA =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(clientWithMemory);

                    //  
                    CoreAISettingsAsset tempSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                    
                    //  execute_lua tool  Programmer
                    AgentMemoryPolicy policyWithLua = new();
                    new AgentBuilder(BuiltInAgentRoleIds.Programmer)
                        .WithMode(AgentMode.ToolsAndChat)
                        .WithMemory(MemoryToolAction.Append)
                        .WithTool(new LuaLlmTool(new TestLuaExecutor(sink), tempSettings, CoreAI.Logging.NullLog.Instance))
                        .Build()
                        .ApplyToPolicy(policyWithLua);

                    AiOrchestrator orch =
                        CreateOrchestrator(capturingLlm, store, policyWithLua, telemetry, composer, sink);

                    //    native tool calling
                    string prompt = "Create a simple item called 'TestDagger' with quality 50. You MUST use the execute_lua tool call.";

                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls] TEST: EXECUTE LUA TOOL");
                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls]  PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ");

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Programmer,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "execute_lua");

                    Debug.Log($"[AllToolCalls]  MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");
                    Debug.Log($"[AllToolCalls] Commands produced: {sink.Items.Count}");

                    //  :      sink
                    if (sink.Items.Count == 0)
                    {
                        Debug.LogWarning($"[AllToolCalls]  EXECUTE LUA FAILED: " +
                                       $"No commands produced. Model responded with text instead of calling execute_lua tool. " +
                                       $"Response: {capturingLlm.LastContent}");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls]  Lua executed, {sink.Items.Count} command(s) produced");
                        foreach (ApplyAiGameCommand cmd in sink.Items)
                        {
                            Debug.Log($"[AllToolCalls]   - Command: {cmd.CommandTypeId}");
                        }
                    }

                    Assert.Greater(sink.Items.Count, 0,
                        "Should produce at least one command. Model must call the execute_lua tool, " +
                        "not respond with text. The Lua code must be valid and produce ApplyAiGameCommand.");
                }

                Debug.Log("[AllToolCalls]  EXECUTE LUA TOOL TEST PASSED ");
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
        ///   ILlmClient     .
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

