using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode scenarios that verify a multi-agent crafting workflow and isolated role memory.
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
    public sealed class MultiAgentCraftingWorkflowPlayModeTests
    {
        private const int LlmTurnTimeoutSeconds = 240;
        private const int LiveModelMaxOutputTokens = 128000;

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

            public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
            {
                return Array.Empty<ChatMessage>();
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
        /// Runs the Creator, CoreMechanicAI, and Programmer roles through a complete crafting workflow.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator MultiAgent_CreatorThenMechanicThenProgrammer_CompleteWorkflow()
        {
            Debug.Log("[MultiAgent]  TEST START: Creator  CoreMechanic  Programmer ");

            // Resolve the configured production-like LLM backend.
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.3f,
                    240,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy);
                // Local GGUF models often re-emit the same memory tool payload across tool-loop iterations;
                // duplicate rejection would abort Programmer (and sometimes other roles) before execute_lua.
                policy.ConfigureRole(BuiltInAgentRoleIds.Creator, allowDuplicateToolCalls: true);
                policy.ConfigureRole(BuiltInAgentRoleIds.CoreMechanic, allowDuplicateToolCalls: true);
                policy.ConfigureRole(BuiltInAgentRoleIds.Programmer, allowDuplicateToolCalls: true);
                string programmerLuaCode = null;
                policy.SetToolsForRole(BuiltInAgentRoleIds.Programmer, new ILlmTool[]
                {
                    new DelegateLlmTool("execute_lua", "Execute generated Lua code",
                        new Action<string>(code => { programmerLuaCode = code ?? ""; }))
                });

                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                // Wrap the client with the shared memory store.
                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);
                CoreAi.ClearToolCallHistory();
                using ToolCallCapture toolCalls = new();

                // Step 1: Creator designs the craft.
                {
                    Debug.Log("[MultiAgent]   1: Creator   ");

                    LogAgentMemory(store, "Creator");

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    using CancellationTokenSource cts = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Creator,
                        Hint = "Design a crafting recipe for a weapon made from these ingredients:\n" +
                               "- Iron (metal, hardness:60, magic:5, rarity:1)\n" +
                               "- Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)\n\n" +
                               "Remember the design summary, then return a compact structured response with item_type, estimated_damage, estimated_fire_damage, and quality.",
                        MaxOutputTokens = LiveModelMaxOutputTokens
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "creator design", cts);

                    LogAgentResponse("creator", sink);
                    LogAgentMemory(store, "Creator");

                    // Verify that Creator wrote memory.
                    Assert.IsTrue(
                        store.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState creatorMem) &&
                        !string.IsNullOrWhiteSpace(creatorMem.Memory),
                        "Creator did not write to memory");

                    Debug.Log($"[MultiAgent]  Creator memory: {creatorMem.Memory}");
                }

                // Step 2: CoreMechanicAI calculates the result.
                {
                    Debug.Log("[MultiAgent]   2: CoreMechanicAI   ");

                    LogAgentMemory(store, "CoreMechanicAI");

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    using CancellationTokenSource cts = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = "Calculate craft result for Iron + Fire Crystal.\n" +
                               "Remember the calculated craft result, then return a structured response with item_name and damage.",
                        MaxOutputTokens = LiveModelMaxOutputTokens
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "mechanic calculation", cts);

                    LogAgentResponse("mechanic", sink);
                    LogAgentMemory(store, "CoreMechanicAI");

                    Assert.IsTrue(
                        store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mechanicMem) &&
                        !string.IsNullOrWhiteSpace(mechanicMem.Memory),
                        "CoreMechanicAI did not write to memory");

                    string mechanicMemory = mechanicMem.Memory;
                    Debug.Log($"[MultiAgent]  CoreMechanicAI memory: {mechanicMemory}");

                    string payload = sink.Items.Count > 0 ? sink.Items[0].JsonPayload : "";
                    string itemName = null;
                    if (!string.IsNullOrEmpty(payload) &&
                        TryExtractJsonStringProperty(payload, "item_name", out string fromJson))
                    {
                        itemName = fromJson;
                    }

                    if (string.IsNullOrEmpty(itemName))
                    {
                        itemName = CraftingMemoryItemNameExtractor.ExtractName(payload);
                    }

                    if (string.IsNullOrEmpty(itemName))
                    {
                        Match match = Regex.Match(mechanicMemory, @"Craft#1:\s*(\w+)");
                        if (match.Success)
                        {
                            itemName = match.Groups[1].Value;
                        }
                    }

                    if (!string.IsNullOrEmpty(itemName))
                    {
                        Debug.Log($"[MultiAgent]  Item name from CoreMechanicAI: '{itemName}'");
                    }
                }

                // Step 3: Programmer generates Lua.
                {
                    Debug.Log("[MultiAgent]   3: Programmer   Lua");

                    LogAgentMemory(store, "Programmer");

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    int toolMark = toolCalls.Count;
                    using CancellationTokenSource cts = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Programmer,
                        Hint = "Generate Lua code for a weapon.\n" +
                               "Execute the Lua through the available tool and return a compact result.",
                        // Live models occasionally reply with the Lua as text instead of invoking the
                        // tool; this step verifies the execute_lua plumbing, so force the invocation.
                        ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                        RequiredToolName = "execute_lua",
                        MaxOutputTokens = LiveModelMaxOutputTokens
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "programmer lua", cts);

                    LogAgentResponse("programmer", sink);
                    LogAgentMemory(store, "Programmer");

                    Assert.Greater(sink.Items.Count, 0, "Programmer should emit at least one response payload.");
                    string programmerPayload = sink.Items[0].JsonPayload ?? string.Empty;
                    Assert.IsFalse(string.IsNullOrWhiteSpace(programmerPayload),
                        "Programmer response payload should not be empty.");
                    Assert.That(programmerPayload, Does.Not.Contain("execute_lua tool is not available"),
                        "Workflow claims execute_lua step, but tool was unavailable.");
                    toolCalls.RequireCompletedToolSince(
                        toolMark, BuiltInAgentRoleIds.Programmer, "execute_lua", "programmer lua");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(programmerLuaCode),
                        "Programmer must execute Lua through the registered execute_lua tool.");
                }

                // Step 4: CoreMechanicAI repeats the craft from memory.
                {
                    Debug.Log("[MultiAgent]   4: CoreMechanicAI    ()");

                    string craft1Memory = "";
                    if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem1))
                    {
                        craft1Memory = mem1.Memory;
                        Debug.Log($"[MultiAgent] Previous craft memory: {craft1Memory}");
                    }

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    using CancellationTokenSource cts = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = "Calculate craft result for Iron + Fire Crystal.\n" +
                               $"YOUR PREVIOUS CRAFT MEMORY: {craft1Memory}\n\n" +
                               "Use the previous craft memory to keep the result consistent, remember this repeat craft, and return a structured response.",
                        MaxOutputTokens = LiveModelMaxOutputTokens
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "mechanic repeat", cts);

                    LogAgentResponse("mechanic repeat", sink);
                    LogAgentMemory(store, "CoreMechanicAI");

                    if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem2))
                    {
                        Debug.Log($"[MultiAgent] Final CoreMechanicAI memory:\n{mem2.Memory}");
                    }
                }

                // Final validation.
                Debug.Log("[MultiAgent]  FINAL VALIDATION ");

                Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState creatorState),
                    "Creator must persist its design memory.");
                Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mechanicState),
                    "CoreMechanicAI must persist craft memory for the repeat-craft step.");
                bool hasProgrammerMemory = store.TryLoad(BuiltInAgentRoleIds.Programmer,
                    out AgentMemoryState programmerState);

                Debug.Log($"[MultiAgent] Creator memory:      {creatorState.Memory}");
                Debug.Log($"[MultiAgent] CoreMechanic memory: {mechanicState.Memory}");
                Debug.Log(
                    $"[MultiAgent] Programmer memory:  {(hasProgrammerMemory ? programmerState.Memory : "(none)")}");

                Assert.AreNotEqual(creatorState.Memory, mechanicState.Memory,
                    "Creator and CoreMechanicAI must have DIFFERENT memory");
                if (hasProgrammerMemory)
                {
                    Assert.AreNotEqual(mechanicState.Memory, programmerState.Memory,
                        "Mechanic and Programmer must have DIFFERENT memory when Programmer stores memory.");
                }

                Debug.Log("[MultiAgent]  Memory isolation verified");
                Debug.Log("[MultiAgent]  TEST PASSED ");
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        /// Runs a shorter Creator and CoreMechanicAI workflow to verify memory isolation.
        /// </summary>
        [UnityTest]
        [Explicit(
            "Targeted shorter duplicate of the full multi-agent workflow; run directly when triaging Creator/CoreMechanic memory isolation, not in mandatory full live-model suite.")]
        [Timeout(600000)]
        public IEnumerator MultiAgent_CreatorThenMechanic_QuickWorkflow()
        {
            Debug.Log("[MultiAgent.Quick]  TEST START: Creator  CoreMechanic ");

            // Resolve the configured production-like LLM backend.
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.3f,
                    240,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy);
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                // Creator designs the craft.
                {
                    Debug.Log("[MultiAgent.Quick] === CREATOR: Design craft ===");
                    LogAgentMemory(store, "Creator");

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    using CancellationTokenSource cts = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Creator,
                        Hint =
                            "Design a weapon from: Iron (hardness:60, rarity:1) + Fire Crystal (magic:85, rarity:4).\n" +
                            "Remember the design summary and return a structured response.",
                        ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                        RequiredToolName = "memory",
                        MaxOutputTokens = LiveModelMaxOutputTokens
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "creator", cts);
                    LogAgentResponse("creator", sink);
                    LogAgentMemory(store, "Creator");

                    bool creatorMemoryOk =
                        store.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState creatorMemQuick) &&
                        !string.IsNullOrWhiteSpace(creatorMemQuick.Memory);

                    if (!creatorMemoryOk && sink.Items.Count == 0)
                    {
                        Assert.Inconclusive(
                            "Creator: no orchestrator output - check LLM / LM Studio (model reload, load errors).");
                    }

                    Assert.IsTrue(creatorMemoryOk, "Creator did not write memory");
                }

                // CoreMechanicAI calculates the result.
                {
                    Debug.Log("[MultiAgent.Quick] === COREMECHANIC: Calculate result ===");
                    LogAgentMemory(store, "CoreMechanicAI");

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    using CancellationTokenSource cts = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = "Calculate weapon from: Iron (hardness:60) + Fire Crystal (magic:85).\n" +
                               "Remember the craft result and return a structured response with item_name, damage, fire_damage.",
                        ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
                        RequiredToolName = "memory",
                        MaxOutputTokens = LiveModelMaxOutputTokens
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "mechanic", cts);
                    LogAgentResponse("mechanic", sink);
                    LogAgentMemory(store, "CoreMechanicAI");

                    bool mechanicMemoryOk =
                        store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mechanicMemQuick) &&
                        !string.IsNullOrWhiteSpace(mechanicMemQuick.Memory);

                    if (!mechanicMemoryOk && sink.Items.Count == 0)
                    {
                        Assert.Inconclusive(
                            "CoreMechanic: no orchestrator output - usually local LLM HTTP errors " +
                            "(e.g. LM Studio \"Model reloaded.\" or model load canceled). Retry when the model is idle.");
                    }

                    Assert.IsTrue(mechanicMemoryOk,
                        "CoreMechanicAI did not write memory (model may have skipped the memory tool).");
                }

                // Final validation.
                Debug.Log("[MultiAgent.Quick]  MEMORY ISOLATION CHECK ");

                AgentMemoryState creatorMem = store.States[BuiltInAgentRoleIds.Creator];
                AgentMemoryState mechanicMem = store.States[BuiltInAgentRoleIds.CoreMechanic];

                Debug.Log($"[MultiAgent.Quick] Creator memory:      {creatorMem.Memory}");
                Debug.Log($"[MultiAgent.Quick] CoreMechanic memory: {mechanicMem.Memory}");

                Assert.AreNotEqual(creatorMem.Memory, mechanicMem.Memory,
                    "Agents must have isolated memory");

                Debug.Log("[MultiAgent.Quick]  Memory isolation verified");
                Debug.Log("[MultiAgent.Quick]  TEST PASSED ");
            }
            finally
            {
                handle.Dispose();
            }
        }

        private static bool TryExtractJsonStringProperty(string text, string propertyName, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(propertyName))
            {
                return false;
            }

            Match m = Regex.Match(text, $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*\"([^\"]*)\"",
                RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return false;
            }

            value = m.Groups[1].Value.Trim();
            return !string.IsNullOrEmpty(value);
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
                new NullAiOrchestrationMetrics(),
                ScriptableObject.CreateInstance<Infrastructure.Llm.CoreAISettingsAsset>());
        }

        #region Logging

        private static void LogAgentMemory(InMemoryStore store, string roleId)
        {
            if (store.TryLoad(roleId, out AgentMemoryState mem) && !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[MultiAgent]  {roleId} MEMORY: {mem.Memory}");
            }
            else
            {
                Debug.Log($"[MultiAgent]  {roleId} MEMORY: (empty)");
            }
        }

        private static void LogAgentResponse(string label, ListSink sink)
        {
            if (sink.Items.Count > 0)
            {
                string payload = sink.Items[0].JsonPayload;
                Debug.Log($"[MultiAgent]  {label} RESPONSE ({payload.Length} chars):\n{payload}");
            }
            else
            {
                Debug.Log($"[MultiAgent]  {label}: No response");
            }
        }

        #endregion

        private sealed class ToolCallCapture : IDisposable
        {
            private readonly IDisposable _subscription;
            private readonly List<LlmToolCallRecord> _records = new();

            public ToolCallCapture()
            {
                _subscription = CoreAi.SubscribeToolCalls(_records.Add);
            }

            public int Count => _records.Count;

            public void Dispose()
            {
                _subscription.Dispose();
            }

            public LlmToolCallRecord RequireCompletedToolSince(int startIndex, string roleId, string toolName,
                string label)
            {
                LlmToolCallRecord record = TryGetCompletedToolSince(startIndex, roleId, toolName);

                if (record == null)
                {
                    string seen = string.Join(", ", _records.Skip(startIndex)
                        .Select(r => $"{r.Info.RoleId}:{r.Info.ToolName}:{r.Status}"));
                    Assert.Fail(
                        $"[{label}] Expected completed tool '{toolName}' for role '{roleId}'. Seen: [{seen}]");
                }

                return record;
            }

            public LlmToolCallRecord TryGetCompletedToolSince(int startIndex, string roleId, string toolName)
            {
                return _records
                    .Skip(startIndex)
                    .LastOrDefault(r =>
                        r.Status == "completed" &&
                        string.Equals(r.Info.RoleId, roleId, StringComparison.Ordinal) &&
                        string.Equals(r.Info.ToolName, toolName, StringComparison.Ordinal));
            }
        }
    }
#endif
}
