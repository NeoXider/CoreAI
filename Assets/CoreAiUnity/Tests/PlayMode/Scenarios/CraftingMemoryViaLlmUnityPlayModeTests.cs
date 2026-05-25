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
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using CoreAI.Session;
using LLMUnity;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode           memory
    ///     .       ,
    ///         .
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
    public sealed class CraftingMemoryViaLlmUnityPlayModeTests
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
                Debug.Log($"[InMemoryStore] Saved for {roleId}: {state.Memory}");
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
                Debug.Log($"[LoggingSink] Received command: {command.CommandTypeId}");
                Debug.Log($"[LoggingSink] Payload:\n{command.JsonPayload}");
                Items.Add(command);
            }
        }

        /// <summary>
        ///     LLMUnity: 3 , AI   
        ///   memory     .
        /// </summary>
        [UnityTest]
        [Timeout(2400000)]
        public IEnumerator CraftingMemoryLlmUnity_ThreeCrafts_AllUnique()
        {
            Debug.Log("[CraftingMemory.LLMUnity] ========================================");
            Debug.Log("[CraftingMemory.LLMUnity] TEST START");
            Debug.Log("[CraftingMemory.LLMUnity] ========================================");

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
                //   LLMUnity    .  HTTP  .
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                Debug.Log($"[CraftingMemory] Using backend: {handle.ResolvedBackend}, Model ready");

                InMemoryStore store = new();

                //  LuaLlmTool    Lua (SecureLuaEnvironment)
                RealLuaExecutor luaExecutor = new();
                LuaLlmTool luaTool = new(luaExecutor, ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                    Logging.NullLog.Instance);

                AgentMemoryPolicy policy = new();
                TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.CoreMechanic);
                // Small models often repeat identical tool payloads; allow duplicates so tool loop is not
                // aborted before assertions (same rationale as CraftingMemoryViaOpenAiPlayModeTests).
                policy.ConfigureRole(BuiltInAgentRoleIds.CoreMechanic, allowDuplicateToolCalls: true);
                //  execute_lua   CoreMechanic
                policy.SetToolsForRole(BuiltInAgentRoleIds.CoreMechanic, new ILlmTool[] { luaTool });

                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                CoreAi.ClearToolCallHistory();
                using ToolCallCapture toolCalls = new();
                List<string> craftedNames = new();

                // =====  1: Iron + Oak =====
                {
                    string prompt = BuildCraftPrompt(1,
                        "Iron (metal, hardness:60, magic:5, rarity:1)",
                        "Oak Wood (wood, hardness:40, magic:10, rarity:1)",
                        store);

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
                            "craft 1", "Ironwood Blade", 20, clientWithMemory, store, policy, telemetry, composer,
                            toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 1");
                    }

                    string executedLua = executeLua.Info.ArgumentsJson;
                    if (!ExtractCraftInfo(executedLua, store, craftedNames, "craft 1", 1))
                    {
                        yield break;
                    }
                }

                // =====  2: Steel + Hardwood =====
                {
                    string prompt = BuildCraftPrompt(2,
                        "Steel (metal, hardness:75, magic:8, rarity:2)",
                        "Hardwood (wood, hardness:50, magic:12, rarity:2)",
                        store);

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
                            "craft 2", "Steelheart Blade", 50, clientWithMemory, store, policy, telemetry, composer,
                            toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 2");
                    }

                    string executedLua = executeLua.Info.ArgumentsJson;
                    if (!ExtractCraftInfo(executedLua, store, craftedNames, "craft 2", 2))
                    {
                        yield break;
                    }
                }

                // =====  3: Mithril + Enchanted Wood =====
                {
                    string prompt = BuildCraftPrompt(3,
                        "Mithril (metal, hardness:70, magic:60, rarity:4)",
                        "Enchanted Wood (wood, hardness:45, magic:70, rarity:3)",
                        store);

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
                            "craft 3", "Mithrilwood Blade", 75, clientWithMemory, store, policy, telemetry, composer,
                            toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 3");
                    }

                    string executedLua = executeLua.Info.ArgumentsJson;
                    if (!ExtractCraftInfo(executedLua, store, craftedNames, "craft 3", 3))
                    {
                        yield break;
                    }
                }

                // =====  4: Steel + Hardwood (  #2)    =====
                {
                    string prompt = BuildDeterministicCraftPrompt(4,
                        "Steel (metal, hardness:75, magic:8, rarity:2)",
                        "Hardwood (wood, hardness:50, magic:12, rarity:2)",
                        store,
                        craftedNames[1]);

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

                    string craft2Name = craftedNames[1];
                    LlmToolCallRecord executeLua = toolCalls.TryGetCompletedToolSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua");
                    if (executeLua == null)
                    {
                        yield return RetryExactExecuteLua(
                            "craft 4", craft2Name, 50, clientWithMemory, store, policy, telemetry, composer,
                            toolCalls);
                        executeLua = toolCalls.RequireCompletedToolSince(
                            toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 4");
                    }

                    string executedLua = executeLua.Info.ArgumentsJson;
                    string craft4Name = CraftingMemoryItemNameExtractor.ExtractName(executedLua);
                    Debug.Log(
                        $"[CraftingMemory.LLMUnity] DETERMINISM CHECK: Craft #2 was '{craft2Name}', Craft #4 is '{craft4Name ?? "unknown"}'");

                    bool isDeterministic = !string.IsNullOrEmpty(craft4Name) &&
                                           CraftingMemoryItemNameExtractor.NamesMatchForDeterminism(craft4Name,
                                               craft2Name);

                    if (!isDeterministic)
                    {
                        Debug.LogWarning(
                            $"[CraftingMemory.LLMUnity]  DETERMINISM FAILED: Craft #4 '{craft4Name}' != Craft #2 '{craft2Name}'");
                    }
                    else
                    {
                        Debug.Log(
                            $"[CraftingMemory.LLMUnity]  DETERMINISM PASS: Craft #4 repeated Craft #2 name '{craft2Name}'");
                    }

                    if (!ExtractCraftInfo(executedLua, store, craftedNames, "craft 4", 4))
                    {
                        yield break;
                    }
                }

                // =====   =====
                Debug.Log("[CraftingMemory.LLMUnity] ");
                Debug.Log("[CraftingMemory.LLMUnity]  FINAL VALIDATION ");
                Debug.Log("[CraftingMemory.LLMUnity] ");

                Assert.AreEqual(4, craftedNames.Count, "Must have 4 crafted items");

                //  1, 2, 3  
                HashSet<string> uniqueFirst3 = new(craftedNames.Take(3).Select(n => n.ToLowerInvariant()));
                Assert.AreEqual(3, uniqueFirst3.Count,
                    $"Crafts 1-3 must be unique! Got: {string.Join(", ", craftedNames.Take(3))}");

                Debug.Log("[CraftingMemory.LLMUnity]  First 3 crafts are unique");

                string craft2Final = craftedNames[1];
                string craft4Final = craftedNames[3];
                Debug.Log($"[CraftingMemory.LLMUnity] Crafted items: {string.Join(" | ", craftedNames)}");
                bool namesMatch = CraftingMemoryItemNameExtractor.NamesMatchForDeterminism(craft2Final, craft4Final);
                Debug.Log(
                    $"[CraftingMemory.LLMUnity] Determinism: Craft#2='{craft2Final}' vs Craft#4='{craft4Final}' " +
                    $" {(namesMatch ? " SAME" : " DIFFERENT")} (whitespace-insensitive)");

                Assert.IsTrue(namesMatch,
                    $"Determinism failed: craft #4 must repeat craft #2 name (whitespace-insensitive). Craft2='{craft2Final}' Craft4='{craft4Final}'");

                if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState finalMem))
                {
                    Debug.Log($"[CraftingMemory.LLMUnity] Final memory state:\n{finalMem.Memory}");
                }

                Debug.Log("[CraftingMemory.LLMUnity] ");
                Debug.Log("[CraftingMemory.LLMUnity]  TEST PASSED ");
                Debug.Log("[CraftingMemory.LLMUnity] ");
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
                new NullAiOrchestrationMetrics(), ScriptableObject.CreateInstance<CoreAISettingsAsset>());
        }

        /// <summary>
        ///  ILuaExecutor  SecureLuaEnvironment   Lua   .
        /// </summary>
        private sealed class RealLuaExecutor : LuaTool.ILuaExecutor
        {
            private readonly SecureLuaEnvironment _sandbox;
            private readonly LuaApiRegistry _registry;

            public RealLuaExecutor()
            {
                _sandbox = new SecureLuaEnvironment();
                _registry = new LuaApiRegistry();

                //   API: report, create_item
                _registry.Register("report", new Action<string>(msg =>
                    Debug.Log($"[Lua.report] {msg}")));
                _registry.Register("create_item", new Action<string, string, double>((name, type, quality) =>
                    Debug.Log($"[Lua.create_item] name={name}, type={type}, quality={quality}")));
                _registry.Register("add", new Func<double, double, double>((a, b) => a + b));
            }

            public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken ct)
            {
                try
                {
                    Script script = _sandbox.CreateScript(_registry);
                    DynValue result = _sandbox.RunChunk(script, code);
                    string output = result?.ToString() ?? "nil";
                    Debug.Log($"[RealLuaExecutor] SUCCESS: {output}");
                    return Task.FromResult(new LuaTool.LuaResult
                    {
                        Success = true,
                        Output = output
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RealLuaExecutor] FAILED: {ex.Message}");
                    return Task.FromResult(new LuaTool.LuaResult
                    {
                        Success = false,
                        Error = ex.Message
                    });
                }
            }
        }

        private static string BuildCraftPrompt(int craftNumber, string ingredient1, string ingredient2,
            InMemoryStore store)
        {
            string header = $"You are crafting a weapon. This is craft #{craftNumber}.\n\n";
            string ingredients = $"Ingredients:\n- {ingredient1}\n- {ingredient2}\n\n";

            //    store   ,     
            string memoryFromStore = "";
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                memoryFromStore = mem.Memory;
            }

            string memorySection = string.IsNullOrEmpty(memoryFromStore)
                ? "This is your first craft. No previous crafts to check.\n\n"
                : $"YOUR MEMORY (previous crafts):\n{memoryFromStore}\n\n" +
                  "CRITICAL: You MUST create a DIFFERENT weapon from all previous crafts above. " +
                  "Do NOT repeat any previous craft name or concept.\n\n";

            string memoryWriteHint = string.IsNullOrEmpty(memoryFromStore)
                ? $"Previous crafts: Craft #{craftNumber} - <your weapon name> made from {ingredient1.Split(' ')[0]} + {ingredient2.Split(' ')[0]}"
                : $"{memoryFromStore}, Craft #{craftNumber} - <your weapon name> made from {ingredient1.Split(' ')[0]} + {ingredient2.Split(' ')[0]}";

            string instructions = "You MUST perform these actions IN ORDER using tool calls:\n\n" +
                                  "STEP 1: Call the 'memory' tool with:\n" +
                                  "  - action: \"write\"\n" +
                                  $"  - content: \"{memoryWriteHint}\"\n\n" +
                                  "STEP 2: Call the 'execute_lua' tool with Lua code:\n" +
                                  "  create_item('YourWeaponName', 'weapon', quality)\n" +
                                  "  report('crafted YourWeaponName')\n\n" +
                                  "Choose a creative weapon name based on the ingredients. " +
                                  "Quality should be 1-100 based on ingredient rarity.\n\n" +
                                  "CRITICAL: You MUST call BOTH tools. Do NOT stop after memory.";

            return header + ingredients + memorySection + instructions;
        }

        private static string BuildDeterministicCraftPrompt(int craftNumber, string ingredient1, string ingredient2,
            InMemoryStore store,
            string exactWeaponNameFromEarlierCraft)
        {
            string header = $"You are crafting a weapon. This is craft #{craftNumber}.\n\n";
            string ingredients = $"Ingredients:\n- {ingredient1}\n- {ingredient2}\n\n";

            string memoryFromStore = "";
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                memoryFromStore = mem.Memory;
            }

            string memorySection = string.IsNullOrEmpty(memoryFromStore)
                ? "This is your first craft.\n\n"
                : $"YOUR MEMORY (ALL previous crafts):\n{memoryFromStore}\n\n";

            string luaLiteral = exactWeaponNameFromEarlierCraft.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);

            string memoryWriteHint = !string.IsNullOrEmpty(memoryFromStore)
                ? $"{memoryFromStore}, Craft #{craftNumber} - {exactWeaponNameFromEarlierCraft}"
                : $"Previous crafts: Craft #{craftNumber} - {exactWeaponNameFromEarlierCraft}";

            string instructions = "You MUST perform these actions IN ORDER using tool calls:\n\n" +
                                  "STEP 1: Call the 'memory' tool with:\n" +
                                  "  - action: \"write\"\n" +
                                  $"  - content: \"{memoryWriteHint}\"\n\n" +
                                  "STEP 2: Call the 'execute_lua' tool with Lua code that ONLY calls the game API, same as crafts 1-3:\n" +
                                  $"  create_item('{luaLiteral}', 'weapon', 50)\n" +
                                  $"  report('crafted {luaLiteral}')\n" +
                                  "Do NOT return Lua tables, do NOT simulate crafting in variables only — the host must see create_item and report.\n\n" +
                                  "CRITICAL: You MUST craft the EXACT SAME item as before - same name, same quality.\n" +
                                  "These are the EXACT same ingredients, so the result must be IDENTICAL.\n" +
                                  "You MUST call BOTH tools. Do NOT stop after memory.";

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
            Debug.LogWarning($"[CraftingMemory.LLMUnity] {label}: execute_lua was not completed; retrying with exact Lua-only tool prompt.");
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

        #region Logging Helpers

        /// <summary>
        /// Lets the orchestrator / MEAI pipeline finish writing <see cref="IAgentMemoryStore"/> after the task completes
        /// so logs and checks see the same memory the next prompt build will use.
        /// </summary>
        private IEnumerator FlushMemoryStorePersistenceFrames()
        {
            for (int i = 0; i < 8; i++)
            {
                yield return null;
            }
        }

        private static void LogBeforeModelCall(string label, string prompt, InMemoryStore store)
        {
            Debug.Log("[CraftingMemory.LLMUnity] ----------------------------------------");
            Debug.Log($"[CraftingMemory.LLMUnity] SENDING TO MODEL: {label}");
            Debug.Log("[CraftingMemory.LLMUnity] ----------------------------------------");

            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.LLMUnity] MEMORY VISIBLE TO MODEL:\n{mem.Memory}");
                Debug.Log("[CraftingMemory.LLMUnity] ----------------------------------------");
            }
            else
            {
                Debug.Log("[CraftingMemory.LLMUnity] MEMORY: (empty - first craft)");
                Debug.Log("[CraftingMemory.LLMUnity] ----------------------------------------");
            }

            Debug.Log($"[CraftingMemory.LLMUnity] PROMPT ({prompt.Length} chars):\n{prompt}");
            Debug.Log("[CraftingMemory.LLMUnity] ----------------------------------------");
        }

        private static void LogAfterModelCall(string label, ListSink sink, InMemoryStore store)
        {
            Debug.Log("[CraftingMemory.LLMUnity] ----------------------------------------");
            Debug.Log($"[CraftingMemory.LLMUnity] MODEL RESPONSE: {label}");
            Debug.Log("[CraftingMemory.LLMUnity] ----------------------------------------");

            if (sink.Items.Count > 0)
            {
                string payload = sink.Items[0].JsonPayload;
                Debug.Log($"[CraftingMemory.LLMUnity] Command received: {sink.Items[0].CommandTypeId}");
                Debug.Log($"[CraftingMemory.LLMUnity] RAW PAYLOAD ({payload.Length} chars):\n{payload}");
            }
            else
            {
                Debug.Log("[CraftingMemory.LLMUnity] NO COMMAND produced");
            }

            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.LLMUnity] MEMORY AFTER:\n{mem.Memory}");
            }
            else
            {
                Debug.Log(
                    "[CraftingMemory.LLMUnity] MEMORY: (empty in store after turn — may still be applied next frame, or filled by ExtractCraftInfo from payload)");
            }

            Debug.Log("[CraftingMemory.LLMUnity] ----------------------------------------");
        }

        private static bool ExtractCraftInfo(
            string executedLuaPayload,
            InMemoryStore store,
            List<string> craftedNames,
            string label,
            int craftNumber)
        {
            // Prefer model-written memory (canonical craft line) before prose payload heuristics.
            string itemName = null;
            if (TryExtractCraftNameFromMemory(store, craftNumber, out string fromMemoryLine))
            {
                itemName = fromMemoryLine;
            }

            if (string.IsNullOrEmpty(itemName))
            {
                itemName = CraftingMemoryItemNameExtractor.ExtractName(executedLuaPayload);
            }

            if (string.IsNullOrEmpty(itemName))
            {
                Debug.LogWarning($"[{label}] Could not extract item name from payload or memory");
                Assert.Inconclusive(
                    $"[{label}] LLM backend did not produce an executable craft result for craft #{craftNumber}. " +
                    $"Payload: {FormatPayloadForAssertion(executedLuaPayload)}");
                return false;
            }

            if (IsSyntheticUnknownName(itemName))
            {
                Assert.Inconclusive(
                    $"[{label}] LLM backend produced only a synthetic fallback craft name for craft #{craftNumber}: '{itemName}'. " +
                    $"Payload: {FormatPayloadForAssertion(executedLuaPayload)}");
                return false;
            }

            craftedNames.Add(itemName);

            //      
            string existingMemory = "";
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState existing) &&
                !string.IsNullOrWhiteSpace(existing.Memory))
            {
                existingMemory = existing.Memory;
            }

            //      (     )  
            if (!existingMemory.Contains(itemName))
            {
                string updatedMemory = string.IsNullOrEmpty(existingMemory)
                    ? $"Previous crafts: Craft #{craftedNames.Count} - {itemName}"
                    : $"{existingMemory}, Craft #{craftedNames.Count} - {itemName}";
                store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState { Memory = updatedMemory });
                Debug.Log($"[{label}] Memory updated: {updatedMemory}");
            }

            Debug.Log($"[{label}] Crafted: '{itemName}'");
            return true;
        }

        private static bool IsSyntheticUnknownName(string itemName)
        {
            return Regex.IsMatch(itemName ?? "", "^unknown(?:_\\d+)?$", RegexOptions.IgnoreCase);
        }

        private static string FormatPayloadForAssertion(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return "<empty>";
            }

            string normalized = payload.Replace("\r", "\\r").Replace("\n", "\\n");
            const int max = 500;
            return normalized.Length <= max ? normalized : normalized.Substring(0, max) + "...";
        }

        private static bool TryExtractCraftNameFromMemory(InMemoryStore store, int craftNumber, out string itemName)
        {
            itemName = null;

            if (!store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) ||
                string.IsNullOrWhiteSpace(mem.Memory))
            {
                return false;
            }

            // Expected format from prompt: "Previous crafts: Craft #N - <weapon name> made from X + Y"
            // We only need the name; keep it permissive against punctuation.
            Match match = Regex.Match(mem.Memory, $"Craft\\s*#\\s*{craftNumber}\\s*-\\s*([^,|]+?)\\s+made\\s+from",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                itemName = match.Groups[1].Value.Trim();
                return !string.IsNullOrWhiteSpace(itemName);
            }

            // Fallback: "Craft #N - Name" until delimiter
            match = Regex.Match(mem.Memory, $"Craft\\s*#\\s*{craftNumber}\\s*-\\s*([^,|]+)",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                itemName = match.Groups[1].Value.Trim();
                return !string.IsNullOrWhiteSpace(itemName);
            }

            return false;
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

            public LlmToolCallRecord RequireCompletedToolSince(int startIndex, string roleId, string toolName, string label)
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
                    Assert.Inconclusive($"[{label}] Expected completed tool '{toolName}' for role '{roleId}'. Seen: [{seen}]");
                }

                return record;
            }
        }
    }
#endif
}
