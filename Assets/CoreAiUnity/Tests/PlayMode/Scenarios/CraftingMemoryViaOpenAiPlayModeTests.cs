using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode      OpenAI API ( LM Studio).
    ///  :          .
    ///       .
    /// </summary>
    public sealed class CraftingMemoryViaOpenAiPlayModeTests
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
                Debug.Log($"[LoggingSink] Received command: {command.CommandTypeId}");
                Debug.Log($"[LoggingSink] Payload:\n{command.JsonPayload}");
                Items.Add(command);
            }
        }

        /// <summary>
        ///     OpenAI HTTP API: 3 , AI
        ///     .    PlayModeOpenAiTestConfig.
        /// </summary>
        [UnityTest]
        [Timeout(2400000)]
        public IEnumerator CraftingMemoryOpenAi_ThreeCrafts_AllUnique()
        {
            Debug.Log("[CraftingMemory.OpenAI] ");
            Debug.Log("[CraftingMemory.OpenAI]  TEST START ");
            Debug.Log("[CraftingMemory.OpenAI] ");

            // Backend  CoreAISettingsAsset (null = FromSettings)
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.3f,
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                Debug.Log("[CraftingMemory.OpenAI]  HTTP client created");
                Debug.Log($"[CraftingMemory.OpenAI] Base URL: {PlayModeOpenAiTestConfig.ResolveBaseUrl()}");
                Debug.Log($"[CraftingMemory.OpenAI] Model: {PlayModeOpenAiTestConfig.ResolveModelId()}");

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.CoreMechanic);
                // Local models often re-emit identical tool payloads across iterations; duplicate guard
                // otherwise hits max consecutive errors. Harness sink is idempotent for these crafts.
                policy.ConfigureRole(BuiltInAgentRoleIds.CoreMechanic, allowDuplicateToolCalls: true);
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                //     MemoryStore (   )
                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                CoreAi.ClearToolCallHistory();
                using ToolCallCapture toolCalls = new();
                List<string> craftedNames = new();
                //  ""  :   craft#    .
                //    store, :
                // -    (  ,    memory tool)
                // -  (craft 4)
                string memoryAccum = "";

                // =====  1: Iron + Oak =====
                {
                    const string ing1 = "Iron";
                    const string ing2 = "Oak";
                    string prompt = BuildCraftPrompt(1,
                        "Iron (metal, hardness:60, magic:5, rarity:1)",
                        "Oak Wood (wood, hardness:40, magic:10, rarity:1)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 1: Iron + Oak", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    int toolMark = toolCalls.Count;
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 1");
                    yield return FlushMemoryStorePersistenceFrames();

                    LogAfterModelCall("craft 1", sink, store);
                    LlmToolCallRecord executeLua = toolCalls.TryGetCompletedToolSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua");
                    if (executeLua == null)
                    {
                        yield return RetryExactExecuteLua(
                            "craft 1", "IronOakSword", 42, clientWithMemory, store, policy, telemetry, composer,
                            toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 1");
                    }

                    string executedLua = executeLua.Info.ArgumentsJson;
                    if (!HasExtractableCraftName(executedLua))
                    {
                        yield return RetryExactExecuteLua(
                            "craft 1", "IronOakSword", 42, clientWithMemory, store, policy, telemetry, composer,
                            toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 1 retry");
                        executedLua = executeLua.Info.ArgumentsJson;
                    }

                    if (!ExtractCraftInfo(executedLua, store, craftedNames, ref memoryAccum, "craft 1", 1, ing1, ing2))
                    {
                        yield break;
                    }
                }

                // =====  2: Steel + Hardwood =====
                {
                    const string ing1 = "Steel";
                    const string ing2 = "Hardwood";
                    string prompt = BuildCraftPrompt(2,
                        "Steel (metal, hardness:75, magic:8, rarity:2)",
                        "Hardwood (wood, hardness:50, magic:12, rarity:2)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 2: Steel + Hardwood", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    int toolMark = toolCalls.Count;
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 2");
                    yield return FlushMemoryStorePersistenceFrames();

                    LogAfterModelCall("craft 2", sink, store);
                    LlmToolCallRecord executeLua = toolCalls.TryGetCompletedToolSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua");
                    if (executeLua == null)
                    {
                        yield return RetryExactExecuteLua(
                            "craft 2", "SteelHardwoodAxe", 75, clientWithMemory, store, policy, telemetry, composer,
                            toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 2");
                    }

                    string executedLua = executeLua.Info.ArgumentsJson;
                    if (!HasExtractableCraftName(executedLua))
                    {
                        yield return RetryExactExecuteLua(
                            "craft 2", "SteelHardwoodAxe", 75, clientWithMemory, store, policy, telemetry, composer,
                            toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 2 retry");
                        executedLua = executeLua.Info.ArgumentsJson;
                    }

                    if (!ExtractCraftInfo(executedLua, store, craftedNames, ref memoryAccum, "craft 2", 2, ing1, ing2))
                    {
                        yield break;
                    }
                }

                // =====  3: Mithril + Enchanted Wood =====
                {
                    const string ing1 = "Mithril";
                    const string ing2 = "Enchanted";
                    string prompt = BuildCraftPrompt(3,
                        "Mithril (metal, hardness:70, magic:60, rarity:4)",
                        "Enchanted Wood (wood, hardness:45, magic:70, rarity:3)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 3: Mithril + Enchanted Wood", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    int toolMark = toolCalls.Count;
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 3");
                    yield return FlushMemoryStorePersistenceFrames();

                    LogAfterModelCall("craft 3", sink, store);
                    LlmToolCallRecord executeLua = toolCalls.TryGetCompletedToolSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua");
                    if (executeLua == null)
                    {
                        yield return RetryExactExecuteLua(
                            "craft 3", "MithrilEnchantedWoodBow", 62, clientWithMemory, store, policy, telemetry,
                            composer, toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 3");
                    }

                    string executedLua = executeLua.Info.ArgumentsJson;
                    if (!HasExtractableCraftName(executedLua))
                    {
                        yield return RetryExactExecuteLua(
                            "craft 3", "MithrilEnchantedWoodBow", 62, clientWithMemory, store, policy, telemetry,
                            composer, toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 3 retry");
                        executedLua = executeLua.Info.ArgumentsJson;
                    }

                    if (!ExtractCraftInfo(executedLua, store, craftedNames, ref memoryAccum, "craft 3", 3, ing1, ing2))
                    {
                        yield break;
                    }
                }

                // =====  4: Steel + Hardwood (  #2)    =====
                {
                    const string ing1 = "Steel";
                    const string ing2 = "Hardwood";
                    string prompt = BuildDeterministicCraftPrompt(4,
                        "Steel (metal, hardness:75, magic:8, rarity:2)",
                        "Hardwood (wood, hardness:50, magic:12, rarity:2)",
                        memoryAccum);

                    LogBeforeModelCall("CRAFT 4: Steel + Hardwood (REPEAT of craft #2  DETERMINISM CHECK)", prompt,
                        store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    int toolMark = toolCalls.Count;
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 4");
                    yield return FlushMemoryStorePersistenceFrames();

                    LogAfterModelCall("craft 4 (determinism)", sink, store);

                    //  :      #2
                    string craft2Name = craftedNames[1]; // Steel+Hardwood   #2
                    LlmToolCallRecord executeLua = toolCalls.TryGetCompletedToolSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua");
                    if (executeLua == null)
                    {
                        yield return RetryExactExecuteLua(
                            "craft 4", craft2Name, 75, clientWithMemory, store, policy, telemetry, composer,
                            toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 4");
                    }

                    string executedLua = executeLua.Info.ArgumentsJson;
                    if (!HasExtractableCraftName(executedLua))
                    {
                        yield return RetryExactExecuteLua(
                            "craft 4", craft2Name, 75, clientWithMemory, store, policy, telemetry, composer,
                            toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 4 retry");
                        executedLua = executeLua.Info.ArgumentsJson;
                    }

                    string craft4Name = CraftingMemoryItemNameExtractor.ExtractName(executedLua);
                    Debug.Log(
                        $"[CraftingMemory.OpenAI] DETERMINISM CHECK: Craft #2 was '{craft2Name}', Craft #4 is '{craft4Name ?? "unknown"}'");

                    bool isDeterministic = !string.IsNullOrEmpty(craft4Name) &&
                                           CraftingMemoryItemNameExtractor.NamesMatchForDeterminism(craft4Name,
                                               craft2Name);

                    if (!isDeterministic)
                    {
                        Debug.LogWarning(
                            $"[CraftingMemory.OpenAI]  DETERMINISM FAILED: Craft #4 '{craft4Name}' != Craft #2 '{craft2Name}'");
                    }
                    else
                    {
                        Debug.Log(
                            $"[CraftingMemory.OpenAI]  DETERMINISM PASS: Craft #4 repeated Craft #2 name '{craft2Name}'");
                    }

                    if (!ExtractCraftInfo(executedLua, store, craftedNames, ref memoryAccum, "craft 4", 4, ing1, ing2))
                    {
                        yield break;
                    }
                }

                // =====   =====
                Debug.Log("[CraftingMemory.OpenAI] ");
                Debug.Log("[CraftingMemory.OpenAI]  FINAL VALIDATION ");
                Debug.Log("[CraftingMemory.OpenAI] ");

                Assert.AreEqual(4, craftedNames.Count, "Must have 4 crafted items");

                //  1, 2, 3
                HashSet<string> uniqueFirst3 = new(craftedNames.Take(3).Select(n => n.ToLowerInvariant()));
                Assert.AreEqual(3, uniqueFirst3.Count,
                    $"Crafts 1-3 must be unique! Got: {string.Join(", ", craftedNames.Take(3))}");

                Debug.Log("[CraftingMemory.OpenAI]  First 3 crafts are unique");

                string craft2Final = craftedNames[1];
                string craft4Final = craftedNames[3];
                Debug.Log($"[CraftingMemory.OpenAI] Crafted items: {string.Join(" | ", craftedNames)}");
                bool namesMatch = CraftingMemoryItemNameExtractor.NamesMatchForDeterminism(craft2Final, craft4Final);
                Debug.Log($"[CraftingMemory.OpenAI] Determinism: Craft#2='{craft2Final}' vs Craft#4='{craft4Final}' " +
                          $" {(namesMatch ? " SAME" : " DIFFERENT")} (whitespace-insensitive)");
                Debug.Log($"[CraftingMemory.OpenAI] Canonical memory for prompts:\n{memoryAccum}");

                Assert.IsTrue(namesMatch,
                    $"Determinism failed: craft #4 must repeat craft #2 name (whitespace-insensitive). Craft2='{craft2Final}' Craft4='{craft4Final}'");
                Debug.Log("[CraftingMemory.OpenAI] ");
                Debug.Log("[CraftingMemory.OpenAI]  TEST PASSED ");
                Debug.Log("[CraftingMemory.OpenAI] ");
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        ///  : 2        .
        ///   .
        /// </summary>
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator CraftingMemoryOpenAi_TwoCrafts_SecondIsDifferent()
        {
            Debug.Log("[CraftingMemory.OpenAI] ");
            Debug.Log("[CraftingMemory.OpenAI]  2-CRAFT TEST START ");
            Debug.Log("[CraftingMemory.OpenAI] ");

            // Backend  CoreAISettingsAsset (null = FromSettings)
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null,
                    0.3f,
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.CoreMechanic);
                policy.ConfigureRole(BuiltInAgentRoleIds.CoreMechanic, allowDuplicateToolCalls: true);
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);
                CoreAi.ClearToolCallHistory();
                using ToolCallCapture toolCalls = new();

                // =====  1 =====
                string prompt1 = BuildCraftPrompt(1,
                    "Steel Ingot (metal, hardness:80, magic:10, rarity:2)",
                    "Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)",
                    "");

                LogBeforeModelCall("CRAFT 1: Steel + Fire Crystal", prompt1, store);

                ListSink sink1 = new();
                AiOrchestrator orch1 = CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink1);

                int toolMark1 = toolCalls.Count;
                Task t1 = orch1.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.CoreMechanic,
                    Hint = prompt1
                });

                yield return PlayModeTestAwait.WaitTask(t1, 300f, "craft 1");
                yield return FlushMemoryStorePersistenceFrames();

                LogAfterModelCall("craft 1", sink1, store);

                LlmToolCallRecord firstExecuteLua = toolCalls.TryGetCompletedToolSince(
                    toolMark1, BuiltInAgentRoleIds.CoreMechanic, "execute_lua");
                if (firstExecuteLua == null)
                {
                    yield return RetryExactExecuteLua(
                        "craft 1", "Firesteel Blade", 75, clientWithMemory, store, policy, telemetry, composer,
                        toolCalls);
                    firstExecuteLua = toolCalls.RequireCompletedToolSince(
                        toolMark1, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 1");
                }

                string firstPayload = firstExecuteLua.Info.ArgumentsJson;
                AssertExecuteLuaUsesNumericQualityIfPresent(firstPayload, "craft 1");
                string firstName = CraftingMemoryItemNameExtractor.ExtractName(firstPayload);
                Debug.Log($"[CraftingMemory.OpenAI] Extracted Craft 1 name: '{firstName ?? "unknown"}'");

                // =====  2 (   + ) =====
                // This harness only registers execute_lua (no memory ILlmTool), so the model's "memory" JSON
                // never hits IAgentMemoryStore. Feed craft #2 the canonical previous-crafts line from craft #1
                // so the prompt can require a different weapon name (same as ThreeCrafts memoryAccum pattern).
                string memoryHint = "";
                if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState st1) &&
                    !string.IsNullOrWhiteSpace(st1.Memory))
                {
                    memoryHint = st1.Memory.Trim();
                }

                if (string.IsNullOrWhiteSpace(memoryHint) && !string.IsNullOrWhiteSpace(firstName))
                {
                    memoryHint = $"Previous crafts: Craft #1 - {firstName} made from Steel + Fire";
                    store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState { Memory = memoryHint });
                    Debug.Log($"[CraftingMemory.OpenAI] Injected harness memory for craft 2 prompt:\n{memoryHint}");
                }

                string prompt2 = BuildCraftPrompt(2,
                    "Steel Ingot (metal, hardness:80, magic:10, rarity:2)",
                    "Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)",
                    memoryHint);

                LogBeforeModelCall("CRAFT 2: Same ingredients, check memory", prompt2, store);

                ListSink sink2 = new();
                AiOrchestrator orch2 = CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink2);

                int toolMark2 = toolCalls.Count;
                Task t2 = orch2.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.CoreMechanic,
                    Hint = prompt2
                });

                yield return PlayModeTestAwait.WaitTask(t2, 300f, "craft 2");
                yield return FlushMemoryStorePersistenceFrames();

                LogAfterModelCall("craft 2", sink2, store);

                LlmToolCallRecord secondExecuteLua = toolCalls.TryGetCompletedToolSince(
                    toolMark2, BuiltInAgentRoleIds.CoreMechanic, "execute_lua");
                if (secondExecuteLua == null)
                {
                    yield return RetryExactExecuteLua(
                        "craft 2", "Flameforge Dagger", 68, clientWithMemory, store, policy, telemetry, composer,
                        toolCalls);
                    secondExecuteLua = toolCalls.RequireCompletedToolSince(
                        toolMark2, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 2");
                }

                string secondPayload = secondExecuteLua.Info.ArgumentsJson;
                AssertExecuteLuaUsesNumericQualityIfPresent(secondPayload, "craft 2");
                string secondName = CraftingMemoryItemNameExtractor.ExtractName(secondPayload);
                Debug.Log($"[CraftingMemory.OpenAI] Extracted Craft 2 name: '{secondName ?? "unknown"}'");

                // ===== :   =====
                Debug.Log("[CraftingMemory.OpenAI] ");
                Debug.Log("[CraftingMemory.OpenAI]  VALIDATION ");
                Debug.Log("[CraftingMemory.OpenAI] ");

                if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(secondName))
                {
                    Assert.AreNotEqual(firstName.ToLowerInvariant(), secondName.ToLowerInvariant(),
                        $"Craft 2 repeated Craft 1 name! Both are '{firstName}'  model did NOT check memory!");

                    Debug.Log($"[CraftingMemory.OpenAI]  Craft names are different:");
                    Debug.Log($"[CraftingMemory.OpenAI]   Craft 1: '{firstName}'");
                    Debug.Log($"[CraftingMemory.OpenAI]   Craft 2: '{secondName}'");
                }
                else
                {
                    Debug.LogWarning("[CraftingMemory.OpenAI]  Could not extract one or both craft names");
                }

                Debug.Log("[CraftingMemory.OpenAI] ");
                Debug.Log("[CraftingMemory.OpenAI]  TEST PASSED ");
                Debug.Log("[CraftingMemory.OpenAI] ");
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
            policy.SetToolsForRole(BuiltInAgentRoleIds.CoreMechanic, new ILlmTool[]
            {
                new DelegateLlmTool("execute_lua", "Execute lua code to create item",
                    new Action<string>(code =>
                    {
                        sink.Publish(new ApplyAiGameCommand
                            { CommandTypeId = AiGameCommandTypeIds.Envelope, JsonPayload = code });
                    }))
            });

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

        private static string BuildCraftPrompt(int craftNumber, string ingredient1, string ingredient2,
            string previousCrafts)
        {
            string header = $"You are crafting a weapon. This is craft #{craftNumber}.\n\n";
            string ingredients = $"Ingredients:\n- {ingredient1}\n- {ingredient2}\n\n";
            string memorySection = string.IsNullOrEmpty(previousCrafts)
                ? "This is your first craft. No previous crafts to check.\n\n"
                : $"YOUR MEMORY (previous crafts):\n{previousCrafts}\n\n" +
                  "CRITICAL: You MUST create a DIFFERENT weapon from all previous crafts above. " +
                  "Do NOT repeat any previous craft name or concept.\n\n";

            string instructions =
                "IMPORTANT: Respond ONLY with tool calls. Do NOT explain your reasoning or think out loud.\n\n" +
                "OUTPUT FORMAT:\n" +
                "1. First, call the memory tool to save this craft:\n" +
                "   ```json\n" +
                "   {\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Previous crafts: <list all crafts including this one>\"}}\n" +
                "   ```\n\n" +
                "2. Then, call the execute_lua tool to create the item:\n" +
                "   ```json\n" +
                "   {\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('YourWeaponName', 'weapon', 42)\\nreport('crafted YourWeaponName')\"}}\n" +
                "   ```\n" +
                "Use an integer literal 1-100 for the third create_item argument (never the identifier quality).\n" +
                "The code field must contain ONLY the Lua code, nothing else.";

            return header + ingredients + memorySection + instructions;
        }

        private IEnumerator RetryExactExecuteLua(
            string label,
            string weaponName,
            int quality,
            ILlmClient client,
            InMemoryStore store,
            AgentMemoryPolicy policy,
            SessionTelemetryCollector telemetry,
            AiPromptComposer composer,
            ToolCallCapture toolCalls)
        {
            string prompt = BuildExactExecuteLuaRetryPrompt(weaponName, quality);
            Debug.LogWarning(
                $"[CraftingMemory.OpenAI] {label}: execute_lua was not completed; retrying with exact Lua-only tool prompt.");
            LogBeforeModelCall($"{label.ToUpperInvariant()} RETRY: exact execute_lua", prompt, store);

            ListSink retrySink = new();
            AiOrchestrator retryOrch = CreateOrchestrator(client, store, policy, telemetry, composer, retrySink);
            Task retry = retryOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.CoreMechanic,
                Hint = prompt
            });

            yield return PlayModeTestAwait.WaitTask(retry, 300f, $"{label} retry execute_lua");
            yield return FlushMemoryStorePersistenceFrames();
            LogAfterModelCall($"{label} retry", retrySink, store);
        }

        private static string BuildExactExecuteLuaRetryPrompt(string weaponName, int quality)
        {
            string luaLiteral = weaponName.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);
            return "The previous answer did not call execute_lua. Do not explain. Do not think out loud. " +
                   "Call ONLY the execute_lua tool now with exactly this Lua code:\n" +
                   $"create_item('{luaLiteral}', 'weapon', {quality})\n" +
                   $"report('crafted {luaLiteral}')";
        }

        private static string BuildDeterministicCraftPrompt(int craftNumber, string ingredient1, string ingredient2,
            string previousCrafts)
        {
            string header = $"You are crafting a weapon. This is craft #{craftNumber}.\n\n";
            string ingredients = $"Ingredients:\n- {ingredient1}\n- {ingredient2}\n\n";
            string memorySection = string.IsNullOrEmpty(previousCrafts)
                ? "This is your first craft.\n\n"
                : $"YOUR MEMORY (ALL previous crafts):\n{previousCrafts}\n\n";

            string instructions =
                "IMPORTANT: Respond ONLY with tool calls. Do NOT explain your reasoning or think out loud.\n\n" +
                "These EXACT ingredients were used before (see memory above).\n" +
                "You MUST craft the EXACT SAME item as before - use the SAME name and properties.\n" +
                "This tests deterministic behavior: same input = same output.\n\n" +
                "OUTPUT FORMAT:\n" +
                "1. Call the memory tool:\n" +
                "   ```json\n" +
                "   {\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Previous crafts: <update list>\"}}\n" +
                "   ```\n\n" +
                "2. Call the execute_lua tool with the EXACT weapon name from your earlier craft with these same ingredients:\n" +
                "   ```json\n" +
                "   {\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('<EXACT_NAME_FROM_MEMORY>', 'weapon', <SAME_QUALITY>)\\nreport('crafted <EXACT_NAME_FROM_MEMORY>')\"}}\n" +
                "   ```\n" +
                "Replace <EXACT_NAME_FROM_MEMORY> with the weapon name you used before for these ingredients. " +
                "Replace <SAME_QUALITY> with the same integer you used before. Do NOT use placeholder text.\n" +
                "The code field must contain ONLY the Lua code, nothing else.";

            return header + ingredients + memorySection + instructions;
        }

        #region Logging Helpers

        private static void AssertExecuteLuaUsesNumericQualityIfPresent(string payload, string label)
        {
            if (string.IsNullOrEmpty(payload) ||
                payload.IndexOf("create_item", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            if (Regex.IsMatch(
                    payload,
                    @",\s*['""]weapon['""]\s*,\s*quality\s*\)",
                    RegexOptions.IgnoreCase))
            {
                Assert.Fail(
                    $"[{label}] create_item must use a numeric quality literal, not the identifier quality. Payload:\n{payload}");
            }

            if (!Regex.IsMatch(
                    payload,
                    @"create_item\s*\([^)]*,\s*['""]weapon['""]\s*,\s*\d+",
                    RegexOptions.IgnoreCase))
            {
                Assert.Fail(
                    $"[{label}] create_item must pass an integer literal as the third argument. Payload:\n{payload}");
            }
        }

        private IEnumerator FlushMemoryStorePersistenceFrames()
        {
            for (int i = 0; i < 8; i++)
            {
                yield return null;
            }
        }

        private static void LogBeforeModelCall(string label, string prompt, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.OpenAI] ");
            Debug.Log($"[CraftingMemory.OpenAI]   SENDING TO MODEL: {label}");
            Debug.Log($"[CraftingMemory.OpenAI] ");

            //  ,
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.OpenAI]   MEMORY VISIBLE TO MODEL:\n{mem.Memory}");
                Debug.Log($"[CraftingMemory.OpenAI] ");
            }
            else
            {
                Debug.Log("[CraftingMemory.OpenAI]   MEMORY: (empty - first craft)");
                Debug.Log($"[CraftingMemory.OpenAI] ");
            }

            Debug.Log($"[CraftingMemory.OpenAI]   PROMPT ({prompt.Length} chars):\n{prompt}");
            Debug.Log($"[CraftingMemory.OpenAI] ");
        }

        private static void LogAfterModelCall(string label, ListSink sink, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.OpenAI] ");
            Debug.Log($"[CraftingMemory.OpenAI]   MODEL RESPONSE: {label}");
            Debug.Log($"[CraftingMemory.OpenAI] ");

            if (sink.Items.Count > 0)
            {
                string payload = sink.Items[0].JsonPayload;
                Debug.Log($"[CraftingMemory.OpenAI]   Command received: {sink.Items[0].CommandTypeId}");
                Debug.Log($"[CraftingMemory.OpenAI]   RAW PAYLOAD ({payload.Length} chars):\n{payload}");
            }
            else
            {
                Debug.Log($"[CraftingMemory.OpenAI]   NO COMMAND produced");
            }

            //
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.OpenAI]   MEMORY AFTER:\n{mem.Memory}");
            }
            else
            {
                Debug.Log(
                    "[CraftingMemory.OpenAI]   MEMORY: (empty in store after turn - harness may sync after ExtractCraftInfo)");
            }

            Debug.Log($"[CraftingMemory.OpenAI] ");
        }

        private static bool ExtractCraftInfo(
            string executedLuaPayload,
            InMemoryStore store,
            List<string> craftedNames,
            ref string memoryAccum,
            string label,
            int craftNumber,
            string ingredient1Short,
            string ingredient2Short)
        {
            string payload = executedLuaPayload ?? "";
            string itemName = CraftingMemoryItemNameExtractor.ExtractName(payload);
            if (string.IsNullOrEmpty(itemName))
            {
                Assert.Inconclusive(
                    $"[{label}] execute_lua completed but item name could not be extracted. Payload: {payload}");
                return false;
            }

            AssertExecuteLuaUsesNumericQualityIfPresent(payload, label);

            craftedNames.Add(itemName);
            memoryAccum = BuildCanonicalMemory(memoryAccum, craftNumber, itemName, ingredient1Short, ingredient2Short);

            // Keep store aligned with memoryAccum (what the next BuildCraftPrompt uses). Do not append
            // "| New: ..." to model-written memory — that made "MEMORY VISIBLE TO MODEL" logs misleading.
            store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState { Memory = memoryAccum });

            Debug.Log($"[{label}]  Crafted: '{itemName}'");
            return true;
        }

        private static bool HasExtractableCraftName(string executedLuaPayload)
        {
            return !string.IsNullOrEmpty(CraftingMemoryItemNameExtractor.ExtractName(executedLuaPayload));
        }

        private static string BuildCanonicalMemory(
            string existing,
            int craftNumber,
            string itemName,
            string ingredient1Short,
            string ingredient2Short)
        {
            string entry = $"Craft #{craftNumber} - {itemName} made from {ingredient1Short} + {ingredient2Short}";
            if (string.IsNullOrWhiteSpace(existing))
            {
                return $"Previous crafts: {entry}";
            }

            // Avoid double-appending the same craft number if model spammed multiple tool calls.
            if (existing.Contains($"Craft #{craftNumber} -", StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            return $"{existing}, {entry}";
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

            public LlmToolCallRecord TryGetCompletedToolSince(int startIndex, string roleId, string toolName)
            {
                return _records
                    .Skip(startIndex)
                    .LastOrDefault(r =>
                        r.Status == "completed" &&
                        string.Equals(r.Info.RoleId, roleId, StringComparison.Ordinal) &&
                        string.Equals(r.Info.ToolName, toolName, StringComparison.Ordinal));
            }

            public void Dispose()
            {
                _subscription.Dispose();
            }

            public LlmToolCallRecord RequireCompletedToolSince(int startIndex, string roleId, string toolName,
                string label)
            {
                LlmToolCallRecord record = _records
                    .Skip(startIndex)
                    .LastOrDefault(r =>
                        r.Status == "completed" &&
                        string.Equals(r.Info.RoleId, roleId, StringComparison.Ordinal) &&
                        string.Equals(r.Info.ToolName, toolName, StringComparison.Ordinal));

                if (record == null)
                {
                    string seen = string.Join(", ", _records.Skip(startIndex)
                        .Select(r => $"{r.Info.RoleId}:{r.Info.ToolName}:{r.Status}"));
                    Assert.Inconclusive(
                        $"[{label}] Expected completed tool '{toolName}' for role '{roleId}'. Seen: [{seen}]");
                }

                return record;
            }
        }
    }

    /// <summary>
    ///        payload.
    ///    : , has been crafted with quality (  <c>with</c>  IgnoreCase)  ..
    /// </summary>
    internal static class CraftingMemoryItemNameExtractor
    {
        private static readonly HashSet<string> JunkSingleWordNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "with", "the", "a", "an", "and", "or", "for", "from", "to", "of", "in", "on", "at", "is", "it", "as", "be",
            "quality", "weapon", "memory", "item", // line "- Weapon created" () vs
            "execute_lua" // tool JSON envelope: "name": "execute_lua" must not become the item name
        };

        private static readonly Regex[] Patterns =
        {
            // Lua: create_item('Name', ...)
            new("create_item\\s*\\(\\s*'([^']+)'"),
            new("create_item\\s*\\(\\s*\"([^\"]+)\""),
            // Markdown craft line used by some local models (before generic **Bold** single-token matches).
            new(@"\*\*Weapon\s+crafted\*\*\s*:\s*([^\r\n*]+?)(?=\s+created\b)", RegexOptions.IgnoreCase),
            // Prose: Created "IronOak Blade" weapon ...
            new("Created\\s+\"([^\"]+)\"\\s+weapon", RegexOptions.IgnoreCase),
            // Prose: memory line in these tests
            new("details for \"([^\"]+)\""),
            // e.g. **Memory updated** with Craft #3 entry for "MithrilEnchant Blade "
            new("entry for \"([^\"]+)\""),
            // "The weapon "SteelHardwood Blade" has been crafted"
            new("(?:[Tt]he )?weapon\\s+\"([^\"]+)\""),
            // " with Craft #4 - SteelHardwood Blade (identical to "
            new("Craft #\\d+\\s*-\\s*([A-Za-z0-9][A-Za-z0-9_ ]*?)\\s*\\("),
            // Qwen3.5 thinking: "Craft #2: SteelHardwoodAxe" or "Craft #2 - SteelHardwoodAxe" (no parens after)
            new("Craft #\\d+\\s*[-:]+\\s*\"?([A-Z][A-Za-z0-9]+(?:[A-Z][a-z]+)+)\"?", RegexOptions.IgnoreCase),
            // Qwen3.5 thinking: 'should be "SteelHardwoodAxe"' or 'use "SteelHardwoodAxe"'
            new("(?:should be|exact name|use|same as)\\s+\"([A-Z][A-Za-z0-9_ ]+)\"", RegexOptions.IgnoreCase),
            // Lua table field inside generated code (before generic JSON "name": tool keys)
            new(@"\bname\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase),
            new(@"\bname\s*=\s*\\""([^""]+)\\""", RegexOptions.IgnoreCase),
            // JSON: "name": "..." (may match tool envelope; junk-filter execute_lua / memory above)
            new("\"name\"\\s*:\\s*\"([^\"]+)\""),
            new("Name\\s*=\\s*\"([^\"]+)\""),
            // Qwen3.5 thinking: quoted PascalCase compound names like "SteelHardwoodAxe"
            new("\"([A-Z][a-z]+(?:[A-Z][a-z]+){1,})\""),
            // " crafted with quality" must NOT match "with" as the name  (?!with\b)
            new("\\bcrafted\\s+(?!with\\b)\\s*\\*{0,2}([A-Za-z][A-Za-z0-9_']*(?:\\s+[A-Za-z][A-Za-z0-9_']*)*)\\*{0,2}",
                RegexOptions.IgnoreCase),
            // Freeform: "X created" (PascalCase multi-part)
            new("\\*{0,2}([A-Z][A-Za-z]{2,}(?:[A-Z][a-z]+)+)\\*{0,2}\\s+(?:created|crafted|forged)",
                RegexOptions.IgnoreCase),
            // Markdown bold: **WeaponName** (one word, after higher-priority patterns)
            new("\\*\\*([A-Z][A-Za-z0-9_]{3,})\\*\\*")
        };

        /// <summary>
        /// Compares craft names for the determinism step: models often drift on whitespace
        /// (e.g. <c>SteelHardwood Spear</c> vs <c>SteelHardwoodSpear</c>).
        /// </summary>
        public static string NormalizeForDeterminismComparison(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            return Regex.Replace(name, @"\s+", "");
        }

        public static bool NamesMatchForDeterminism(string a, string b)
        {
            return string.Equals(
                NormalizeForDeterminismComparison(a),
                NormalizeForDeterminismComparison(b),
                StringComparison.OrdinalIgnoreCase);
        }

        public static string ExtractName(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return null;
            }

            foreach (Regex regex in Patterns)
            {
                foreach (Match match in regex.Matches(payload))
                {
                    if (!match.Success)
                    {
                        continue;
                    }

                    string name = match.Groups[1].Value.Trim();
                    if (string.IsNullOrEmpty(name) || IsJunkName(name))
                    {
                        continue;
                    }

                    return name;
                }
            }

            return null;
        }

        private static bool IsJunkName(string name)
        {
            if (JunkSingleWordNames.Contains(name))
            {
                return true;
            }

            //  ""
            if (name.Length <= 1)
            {
                return true;
            }

            return false;
        }
    }
}