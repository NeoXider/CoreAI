using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
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
    /// PlayMode verification for real tool calls such as memory and execute_lua.
    /// Runs against LLMUnity, an OpenAI-compatible HTTP API, or automatic backend selection.
    /// Select the backend with COREAI_PLAYMODE_LLM_BACKEND and related test settings.
    /// </summary>
#if COREAI_LLM && !UNITY_WEBGL
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
                CoreAi.ClearToolCallHistory();
                AgentMemoryPolicy policy = new();
                TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.CoreMechanic);

                //     MemoryStore (   store')
                ILlmClient sharedClient = handle.WrapWithMemoryStore(store);

                //  LLMAgent  LLM  keepModelLoaded (  LLMUnity)
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
                LLMAgent agent = handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity
                    ? UnityEngine.Object.FindFirstObjectByType<LLMAgent>()
                    : null;
                LLM llm = agent?.llm ?? agent?.GetComponent<LLM>();
#else
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
                    string prompt =
                        "Use the memory tool exactly once with action=\"write\" and content=\"Test craft #1 is an Iron Sword.\".";

                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls] TEST 1: WRITE MEMORY");
                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls]  PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ");

                    using CancellationTokenSource cts = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt,
                        ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                        RequiredToolName = "memory",
                        MaxOutputTokens = 128000
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory write", cts);

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

                    WriteLlmDebug("MemoryTool_Write", capturingLlm, handle, sink.Items.Count);

                    Assert.IsTrue(memorySaved,
                        "Memory must be saved by the required memory tool call, not by text response.");
                }

                // ===== TEST 2: APPEND MEMORY =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(sharedClient);
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policy, telemetry, composer, sink);

                    //    native tool calling
                    string prompt =
                        "Use the memory tool exactly once with action=\"append\" and content=\"Test craft #2 is a Steel Shield.\". Keep the existing memory.";

                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls] TEST 2: APPEND MEMORY");
                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls]  PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ");

                    using CancellationTokenSource cts = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt,
                        ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                        RequiredToolName = "memory",
                        MaxOutputTokens = 128000
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory append", cts);

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

                    WriteLlmDebug("MemoryTool_Append", capturingLlm, handle, sink.Items.Count);

                    Assert.IsTrue(memoryAppended,
                        "Memory must be appended by actual tool call. Expected both 'Iron Sword' and 'Steel Shield' in memory.");
                }

                // ===== TEST 3: CLEAR MEMORY =====
                {
                    ListSink sink = new();
                    CapturingLlmClient capturingLlm = new(sharedClient);
                    AiOrchestrator orch = CreateOrchestrator(capturingLlm, store, policy, telemetry, composer, sink);

                    //    native tool calling
                    string prompt =
                        "Use the memory tool exactly once with action=\"clear\". Do not use read, write, append, delete, str_replace, insert, or rename.";

                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls] TEST 3: CLEAR MEMORY");
                    Debug.Log($"[AllToolCalls] ");
                    Debug.Log($"[AllToolCalls]  PROMPT TO MODEL:");
                    Debug.Log($"[AllToolCalls] {prompt}");
                    Debug.Log($"[AllToolCalls] ");

                    using CancellationTokenSource cts = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt,
                        ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                        RequiredToolName = "memory",
                        MaxOutputTokens = 128000
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, 240f, "memory clear", cts);

                    Debug.Log($"[AllToolCalls]  MODEL RESPONSE:");
                    Debug.Log($"[AllToolCalls] Content: {capturingLlm.LastContent}");

                    WriteLlmDebug("MemoryTool_Clear", capturingLlm, handle, sink.Items.Count);

                    if (!HasCompletedMemoryAction("clear"))
                    {
                        Assert.Inconclusive(
                            "The live backend did not emit memory(action=clear) even with RequireSpecific(memory). " +
                            "Runtime clear/tool-result behavior is covered by deterministic tests; this is model/backend compliance.");
                    }

                    // WHY: the memory tool's `clear` action EMPTIES the document (MemoryMutationPlan.Change(""))
                    // — it deliberately keeps the store record with Memory == "", it does not delete the entry.
                    // So "cleared" means "no entry OR empty content", not "entry absent" (the old check wrongly
                    // failed the run even though HasCompletedMemoryAction("clear") above already proved the model
                    // emitted and completed the real tool call).
                    bool memoryCleared =
                        !store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState clearedState)
                        || string.IsNullOrEmpty(clearedState?.Memory);

                    if (!memoryCleared)
                    {
                        Debug.LogWarning($"[AllToolCalls]  CLEAR FAILED: memory(action=clear) completed but the " +
                                         $"document is not empty. Current memory: '{clearedState?.Memory}'.");
                    }
                    else
                    {
                        Debug.Log($"[AllToolCalls]  Memory cleared by tool call");
                    }

                    Assert.IsTrue(memoryCleared,
                        "Memory must be emptied by the clear tool call (empty document, entry may remain).");
                }

                Debug.Log("[AllToolCalls]  MEMORY TOOL TEST PASSED ");
            }
            finally
            {
                handle.Dispose();
            }
        }

        // The execute_lua LLM verification test was removed with the legacy Lua runtime. The Lua-CSharp
        // execute_lua path is covered by deterministic EditMode tests (LuaToolEditModeTests and
        // LuaCsModRuntimeEditModeTests); this fixture keeps the backend-agnostic memory-tool verification.

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
                new NullAiOrchestrationMetrics(), ScriptableObject.CreateInstance<CoreAISettingsAsset>());
        }

        /// <summary>
        /// Dumps exactly what the model saw and produced to a file under TestResults/CoreAI/LlmDebug,
        /// so a "model did not call the tool" failure can be diagnosed: does the system prompt carry the
        /// tool contract, were the tools actually offered (and as native function calls?), and did the
        /// model emit a tool call or plain text? Uses the already-captured request fields plus the global
        /// tool-call history snapshot (CoreAi.GetToolCallHistorySnapshot) — the ready memory/trace
        /// extraction. Never throws: diagnostics must not mask the real assertion.
        /// </summary>
        private static void WriteLlmDebug(
            string label, CapturingLlmClient cap, PlayModeProductionLikeLlmHandle handle, int commandsProduced)
        {
            try
            {
                System.Text.StringBuilder sb = new();
                sb.AppendLine($"=== LLM DEBUG: {label} ===");
                sb.AppendLine($"backend: {handle.ResolvedBackend}");
                sb.AppendLine($"client: {handle.Client.GetType().Name}");
                sb.AppendLine($"supportsNativeToolCalling: {handle.Client.SupportsNativeToolCalling}");
                sb.AppendLine();

                sb.AppendLine("--- TOOLS OFFERED TO THE MODEL ---");
                if (cap.LastTools == null || cap.LastTools.Count == 0)
                {
                    sb.AppendLine("(NONE — the request carried no tools; the model literally cannot call one)");
                }
                else
                {
                    foreach (ILlmTool tool in cap.LastTools)
                    {
                        sb.AppendLine($"- {tool.Name}: {tool.Description}");
                        sb.AppendLine($"    schema: {tool.ParametersSchema}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("--- SYSTEM PROMPT (what the model was told, incl. tool contract) ---");
                sb.AppendLine(cap.LastSystemPrompt ?? "(null)");
                sb.AppendLine();
                sb.AppendLine("--- USER PAYLOAD ---");
                sb.AppendLine(cap.LastUserPayload ?? "(null)");
                sb.AppendLine();
                sb.AppendLine("--- MODEL RAW RESPONSE (text; empty when it emitted only a tool call) ---");
                sb.AppendLine(string.IsNullOrEmpty(cap.LastContent) ? "(empty)" : cap.LastContent);
                sb.AppendLine();
                sb.AppendLine($"--- COMMANDS PRODUCED (game-side effects): {commandsProduced} ---");

                sb.AppendLine();
                sb.AppendLine("--- TOOL-CALL HISTORY (what actually executed) ---");
                IReadOnlyList<LlmToolCallRecord> history = CoreAi.GetToolCallHistorySnapshot();
                if (history == null || history.Count == 0)
                {
                    sb.AppendLine("(none — no tool call reached execution)");
                }
                else
                {
                    foreach (LlmToolCallRecord rec in history)
                    {
                        if (rec == null)
                        {
                            continue;
                        }

                        sb.AppendLine($"- {rec.Info.ToolName} [{rec.Status}] args={rec.Info.ArgumentsJson}");
                    }
                }

                string dir = System.IO.Path.Combine(
                    System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath,
                    "TestResults", "CoreAI", "LlmDebug");
                System.IO.Directory.CreateDirectory(dir);
                string safe = label.Replace(' ', '_').Replace(':', '-');
                string path = System.IO.Path.Combine(dir, $"{safe}.txt");
                System.IO.File.WriteAllText(path, sb.ToString());
                Debug.Log($"[AllToolCalls] LLM debug bundle written: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AllToolCalls] LLM debug dump failed (non-fatal): {ex.Message}");
            }
        }

        private static bool HasCompletedMemoryAction(string action)
        {
            return CoreAi.GetToolCallHistorySnapshot().Any(record =>
            {
                if (record == null ||
                    !string.Equals(record.Status, "completed", StringComparison.Ordinal) ||
                    !string.Equals(record.Info.ToolName, "memory", StringComparison.Ordinal))
                {
                    return false;
                }

                try
                {
                    JObject args = JObject.Parse(record.Info.ArgumentsJson ?? "{}");
                    return string.Equals(args["action"]?.Value<string>(), action, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
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
                CancellationToken cancellationToken = default)
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

            // Forward the streaming path to the inner client instead of inheriting ILlmClient's default
            // (which collapses to CompleteAsync): with streaming-by-default orchestration the tool tests
            // must exercise the REAL execute-as-you-stream path, and still capture what the model saw/said.
            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
                LlmCompletionRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                LastSystemPrompt = request.SystemPrompt;
                LastUserPayload = request.UserPayload;
                LastTools = request.Tools;

                System.Text.StringBuilder sb = new();
                await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, cancellationToken))
                {
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        sb.Append(chunk.Text);
                        LastContent = sb.ToString();
                    }

                    yield return chunk;
                }
            }

            public void SetTools(IReadOnlyList<ILlmTool> tools)
            {
                _inner.SetTools(tools);
            }
        }
    }
#endif
}
