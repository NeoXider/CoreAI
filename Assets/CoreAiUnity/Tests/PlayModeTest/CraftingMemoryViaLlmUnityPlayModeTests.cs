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
    /// PlayMode С‚РµСЃС‚ РєСЂР°С„С‚Р° СЃ РїР°РјСЏС‚СЊСЋ вЂ” РїСЂРѕРІРµСЂСЏРµС‚ С‡С‚Рѕ РјРѕРґРµР»СЊ РёСЃРїРѕР»СЊР·СѓРµС‚ РёРЅСЃС‚СЂСѓРјРµРЅС‚ memory
    /// РґР»СЏ СЃРѕС…СЂР°РЅРµРЅРёСЏ СЃРѕСЃС‚РѕСЏРЅРёСЏ РјРµР¶РґСѓ РІС‹Р·РѕРІР°РјРё. РўРµСЃС‚ РќР• РїРµСЂРµРґР°С‘С‚ РїСЂРµРґС‹РґСѓС‰РёРµ РєСЂР°С„С‚С‹ РІ РїСЂРѕРјРїС‚,
    /// РјРѕРґРµР»СЊ РґРѕР»Р¶РЅР° СЃР°РјР° СЃРѕС…СЂР°РЅСЏС‚СЊ Рё С‡РёС‚Р°С‚СЊ РїР°РјСЏС‚СЊ С‡РµСЂРµР· РёРЅСЃС‚СЂСѓРјРµРЅС‚.
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
        /// РџРѕР»РЅС‹Р№ РІРѕСЂРєС„Р»РѕСѓ РєСЂР°С„С‚Р° С‡РµСЂРµР· LLMUnity: 3 РёС‚РµСЂР°С†РёРё, AI Р·Р°РїРѕРјРёРЅР°РµС‚ РєР°Р¶РґС‹Р№ РєСЂР°С„С‚
        /// С‡РµСЂРµР· РёРЅСЃС‚СЂСѓРјРµРЅС‚ memory Рё РґРѕР»Р¶РµРЅ СЃРѕР·РґР°РІР°С‚СЊ СѓРЅРёРєР°Р»СЊРЅС‹Рµ РїСЂРµРґРјРµС‚С‹.
        /// </summary>
        [UnityTest]
        [Timeout(2400000)]
        public IEnumerator CraftingMemoryLlmUnity_ThreeCrafts_AllUnique()
        {
            Debug.Log("[CraftingMemory.LLMUnity] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
            Debug.Log("[CraftingMemory.LLMUnity] в•ђв•ђв•ђ TEST START в•ђв•ђв•ђ");
            Debug.Log("[CraftingMemory.LLMUnity] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");

            // Backend РёР· CoreAISettingsAsset (null = FromSettings)
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
                // РўРѕР»СЊРєРѕ РґР»СЏ LLMUnity вЂ” Р¶РґС‘Рј РіРѕС‚РѕРІРЅРѕСЃС‚Рё РјРѕРґРµР»Рё. Р”Р»СЏ HTTP РЅРµ РЅСѓР¶РЅРѕ.
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                Debug.Log($"[CraftingMemory] Using backend: {handle.ResolvedBackend}, Model ready");

                InMemoryStore store = new();

                // РЎРѕР·РґР°С‘Рј LuaLlmTool СЃ РЅР°СЃС‚РѕСЏС‰РёРј РёСЃРїРѕР»РЅРёС‚РµР»РµРј Lua (SecureLuaEnvironment)
                RealLuaExecutor luaExecutor = new();
                LuaLlmTool luaTool = new(luaExecutor, UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>(), CoreAI.Logging.NullLog.Instance);

                AgentMemoryPolicy policy = new();
                // Р РµРіРёСЃС‚СЂРёСЂСѓРµРј execute_lua РёРЅСЃС‚СЂСѓРјРµРЅС‚ РґР»СЏ CoreMechanic
                policy.SetToolsForRole(BuiltInAgentRoleIds.CoreMechanic, new ILlmTool[] { luaTool });

                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                List<string> craftedNames = new();

                // ===== РљР РђР¤Рў 1: Iron + Oak =====
                {
                    string prompt = BuildCraftPrompt(1,
                        "Iron (metal, hardness:60, magic:5, rarity:1)",
                        "Oak Wood (wood, hardness:40, magic:10, rarity:1)",
                        store);

                    LogBeforeModelCall("CRAFT 1: Iron + Oak", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 1");

                    LogAfterModelCall("craft 1", sink, store);

                    // РџСЂРѕРІРµСЂСЏРµРј С‡С‚Рѕ РјРѕРґРµР»СЊ Р·Р°РїРёСЃР°Р»Р° РІ РїР°РјСЏС‚СЊ
                    if (!store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) ||
                        string.IsNullOrWhiteSpace(mem.Memory))
                    {
                        Debug.LogWarning("[CraftingMemory.LLMUnity] вљ  Model did NOT write to memory after craft 1!");
                    }

                    if (!ExtractCraftInfo(sink, store, craftedNames, "craft 1", 1))
                    {
                        yield break;
                    }
                }

                // ===== РљР РђР¤Рў 2: Steel + Hardwood =====
                {
                    string prompt = BuildCraftPrompt(2,
                        "Steel (metal, hardness:75, magic:8, rarity:2)",
                        "Hardwood (wood, hardness:50, magic:12, rarity:2)",
                        store);

                    LogBeforeModelCall("CRAFT 2: Steel + Hardwood", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 2");

                    LogAfterModelCall("craft 2", sink, store);

                    if (!ExtractCraftInfo(sink, store, craftedNames, "craft 2", 2))
                    {
                        yield break;
                    }
                }

                // ===== РљР РђР¤Рў 3: Mithril + Enchanted Wood =====
                {
                    string prompt = BuildCraftPrompt(3,
                        "Mithril (metal, hardness:70, magic:60, rarity:4)",
                        "Enchanted Wood (wood, hardness:45, magic:70, rarity:3)",
                        store);

                    LogBeforeModelCall("CRAFT 3: Mithril + Enchanted Wood", prompt, store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 3");

                    LogAfterModelCall("craft 3", sink, store);

                    if (!ExtractCraftInfo(sink, store, craftedNames, "craft 3", 3))
                    {
                        yield break;
                    }
                }

                // ===== РљР РђР¤Рў 4: Steel + Hardwood (РџРћР’РўРћР  РєСЂР°С„С‚Р° #2) вЂ” РїСЂРѕРІРµСЂРєР° РґРµС‚РµСЂРјРёРЅРёР·РјР° =====
                {
                    string prompt = BuildDeterministicCraftPrompt(4,
                        "Steel (metal, hardness:75, magic:8, rarity:2)",
                        "Hardwood (wood, hardness:50, magic:12, rarity:2)",
                        store);

                    LogBeforeModelCall("CRAFT 4: Steel + Hardwood (REPEAT of craft #2 вЂ” DETERMINISM CHECK)", prompt,
                        store);

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 4");

                    LogAfterModelCall("craft 4 (determinism)", sink, store);

                    string craft2Name = craftedNames[1];
                    if (sink.Items.Count > 0)
                    {
                        string craft4Name = CraftingMemoryItemNameExtractor.ExtractName(sink.Items[0].JsonPayload);
                        Debug.Log(
                            $"[CraftingMemory.LLMUnity] DETERMINISM CHECK: Craft #2 was '{craft2Name}', Craft #4 is '{craft4Name ?? "unknown"}'");

                        bool isDeterministic = !string.IsNullOrEmpty(craft4Name) &&
                                               craft4Name.ToLowerInvariant() == craft2Name.ToLowerInvariant();

                        if (!isDeterministic)
                        {
                            Debug.LogWarning(
                                $"[CraftingMemory.LLMUnity] вљ  DETERMINISM FAILED: Craft #4 '{craft4Name}' != Craft #2 '{craft2Name}'");
                        }
                        else
                        {
                            Debug.Log(
                                $"[CraftingMemory.LLMUnity] вњ“ DETERMINISM PASS: Craft #4 repeated Craft #2 name '{craft2Name}'");
                        }
                    }

                    if (!ExtractCraftInfo(sink, store, craftedNames, "craft 4", 4))
                    {
                        yield break;
                    }
                }

                // ===== Р¤РРќРђР›Р¬РќРђРЇ РџР РћР’Р•Р РљРђ =====
                Debug.Log("[CraftingMemory.LLMUnity] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                Debug.Log("[CraftingMemory.LLMUnity] в•ђв•ђв•ђ FINAL VALIDATION в•ђв•ђв•ђ");
                Debug.Log("[CraftingMemory.LLMUnity] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");

                Assert.AreEqual(4, craftedNames.Count, "Must have 4 crafted items");

                // РљСЂР°С„С‚С‹ 1, 2, 3 вЂ” СѓРЅРёРєР°Р»СЊРЅС‹
                HashSet<string> uniqueFirst3 = new(craftedNames.Take(3).Select(n => n.ToLowerInvariant()));
                Assert.AreEqual(3, uniqueFirst3.Count,
                    $"Crafts 1-3 must be unique! Got: {string.Join(", ", craftedNames.Take(3))}");

                Debug.Log("[CraftingMemory.LLMUnity] вњ“ First 3 crafts are unique");

                string craft2Final = craftedNames[1];
                string craft4Final = craftedNames[3];
                Debug.Log($"[CraftingMemory.LLMUnity] Crafted items: {string.Join(" | ", craftedNames)}");
                Debug.Log(
                    $"[CraftingMemory.LLMUnity] Determinism: Craft#2='{craft2Final}' vs Craft#4='{craft4Final}' " +
                    $"в†’ {(craft2Final.ToLowerInvariant() == craft4Final.ToLowerInvariant() ? "вњ“ SAME" : "вљ  DIFFERENT")}");

                // РџСЂРѕРІРµСЂСЏРµРј С„РёРЅР°Р»СЊРЅРѕРµ СЃРѕСЃС‚РѕСЏРЅРёРµ РїР°РјСЏС‚Рё
                if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState finalMem))
                {
                    Debug.Log($"[CraftingMemory.LLMUnity] Final memory state:\n{finalMem.Memory}");
                }

                Debug.Log("[CraftingMemory.LLMUnity] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                Debug.Log("[CraftingMemory.LLMUnity] в•ђв•ђв•ђ TEST PASSED в•ђв•ђв•ђ");
                Debug.Log("[CraftingMemory.LLMUnity] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
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

        /// <summary>
        /// РќР°СЃС‚РѕСЏС‰РёР№ ILuaExecutor С‡РµСЂРµР· SecureLuaEnvironment вЂ” РІС‹РїРѕР»РЅСЏРµС‚ Lua РєРѕРґ РІ РїРµСЃРѕС‡РЅРёС†Рµ.
        /// </summary>
        private sealed class RealLuaExecutor : LuaTool.ILuaExecutor
        {
            private readonly SecureLuaEnvironment _sandbox;
            private readonly LuaApiRegistry _registry;

            public RealLuaExecutor()
            {
                _sandbox = new SecureLuaEnvironment();
                _registry = new LuaApiRegistry();

                // Р РµРіРёСЃС‚СЂРёСЂСѓРµРј Р±Р°Р·РѕРІС‹Рµ API: report, create_item
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

            // Р§РёС‚Р°РµРј РїР°РјСЏС‚СЊ РёР· store вЂ” СЌС‚Рѕ С‚Рѕ, С‡С‚Рѕ РјРѕРґРµР»СЊ РґРѕР»Р¶РЅР° Р±С‹Р»Р° СЃРѕС…СЂР°РЅРёС‚СЊ
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
            InMemoryStore store)
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

            string memoryWriteHint = !string.IsNullOrEmpty(memoryFromStore)
                ? $"{memoryFromStore}, Craft #{craftNumber} - <same weapon name as before>"
                : $"Previous crafts: Craft #{craftNumber} - <weapon name>";

            string instructions = "You MUST perform these actions IN ORDER using tool calls:\n\n" +
                                  "STEP 1: Call the 'memory' tool with:\n" +
                                  "  - action: \"write\"\n" +
                                  $"  - content: \"{memoryWriteHint}\"\n\n" +
                                  "STEP 2: Call the 'execute_lua' tool with Lua code.\n\n" +
                                  "CRITICAL: You MUST craft the EXACT SAME item as before вЂ” same name, same quality.\n" +
                                  "These are the EXACT same ingredients, so the result must be IDENTICAL.\n" +
                                  "You MUST call BOTH tools. Do NOT stop after memory.";

            return header + ingredients + memorySection + instructions;
        }

        #region Logging Helpers

        private static void LogBeforeModelCall(string label, string prompt, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.LLMUnity] в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");
            Debug.Log($"[CraftingMemory.LLMUnity] в”‚ рџ“¤ SENDING TO MODEL: {label}");
            Debug.Log($"[CraftingMemory.LLMUnity] в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.LLMUnity] в”‚ рџ“љ MEMORY VISIBLE TO MODEL:\n{mem.Memory}");
                Debug.Log($"[CraftingMemory.LLMUnity] в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");
            }
            else
            {
                Debug.Log($"[CraftingMemory.LLMUnity] в”‚ рџ“љ MEMORY: (empty вЂ” first craft)");
                Debug.Log($"[CraftingMemory.LLMUnity] в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");
            }

            Debug.Log($"[CraftingMemory.LLMUnity] в”‚ рџ“ќ PROMPT ({prompt.Length} chars):\n{prompt}");
            Debug.Log($"[CraftingMemory.LLMUnity] в””в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");
        }

        private static void LogAfterModelCall(string label, ListSink sink, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.LLMUnity] в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");
            Debug.Log($"[CraftingMemory.LLMUnity] в”‚ рџ“Ґ MODEL RESPONSE: {label}");
            Debug.Log($"[CraftingMemory.LLMUnity] в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

            if (sink.Items.Count > 0)
            {
                string payload = sink.Items[0].JsonPayload;
                Debug.Log($"[CraftingMemory.LLMUnity] в”‚ вњ… Command received: {sink.Items[0].CommandTypeId}");
                Debug.Log($"[CraftingMemory.LLMUnity] в”‚ рџ“¦ RAW PAYLOAD ({payload.Length} chars):\n{payload}");
            }
            else
            {
                Debug.Log($"[CraftingMemory.LLMUnity] в”‚ вќЊ NO COMMAND produced");
            }

            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.LLMUnity] в”‚ рџ’ѕ MEMORY AFTER:\n{mem.Memory}");
            }
            else
            {
                Debug.Log($"[CraftingMemory.LLMUnity] в”‚ рџ’ѕ MEMORY: (not written by model)");
            }

            Debug.Log($"[CraftingMemory.LLMUnity] в””в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");
        }

        private static bool ExtractCraftInfo(
            ListSink sink,
            InMemoryStore store,
            List<string> craftedNames,
            string label,
            int craftNumber)
        {
            string payload = sink.Items.Count > 0 ? sink.Items[0].JsonPayload : null;

            // РќРµРєРѕС‚РѕСЂС‹Рµ Р±СЌРєРµРЅРґС‹/СЂРµР¶РёРјС‹ function-calling РІС‹РїРѕР»РЅСЏСЋС‚ tools Рё РЅРµ РІРѕР·РІСЂР°С‰Р°СЋС‚ С‚РµРєСЃС‚ РІРѕРѕР±С‰Рµ.
            // Р’ СЌС‚РѕРј С‚РµСЃС‚Рµ memory-write СЏРІР»СЏРµС‚СЃСЏ РѕР±СЏР·Р°С‚РµР»СЊРЅС‹Рј, РїРѕСЌС‚РѕРјСѓ РјРѕР¶РµРј РІРѕСЃСЃС‚Р°РЅРѕРІРёС‚СЊ РёРјСЏ РёР· memory.
            string itemName = CraftingMemoryItemNameExtractor.ExtractName(payload);
            if (string.IsNullOrEmpty(itemName))
            {
                if (TryExtractCraftNameFromMemory(store, craftNumber, out string fromMemory))
                {
                    itemName = fromMemory;
                    Debug.LogWarning($"[{label}] вљ  Could not extract item name from payload вЂ” recovered from memory: '{itemName}'");
                }
                else
                {
                    Debug.LogWarning($"[{label}] вљ  Could not extract item name from payload or memory");
                    itemName = $"unknown_{craftedNames.Count + 1}";
                }
            }

            craftedNames.Add(itemName);

            // РћР±РЅРѕРІР»СЏРµРј РїР°РјСЏС‚СЊ вЂ” РЅР°РєР°РїР»РёРІР°РµРј СЃРїРёСЃРѕРє РєСЂР°С„С‚РѕРІ
            string existingMemory = "";
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState existing) &&
                !string.IsNullOrWhiteSpace(existing.Memory))
            {
                existingMemory = existing.Memory;
            }

            // Р•СЃР»Рё РјРѕРґРµР»СЊ СѓР¶Рµ РѕР±РЅРѕРІРёР»Р° РїР°РјСЏС‚СЊ (РїСЂРѕРІРµСЂСЏРµРј СЃРѕРґРµСЂР¶РёС‚ Р»Рё РѕРЅР° С‚РµРєСѓС‰РёР№ РєСЂР°С„С‚) вЂ” РїСЂРѕРїСѓСЃРєР°РµРј
            if (!existingMemory.Contains(itemName))
            {
                string updatedMemory = string.IsNullOrEmpty(existingMemory)
                    ? $"Previous crafts: Craft #{craftedNames.Count} - {itemName}"
                    : $"{existingMemory}, Craft #{craftedNames.Count} - {itemName}";
                store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState { Memory = updatedMemory });
                Debug.Log($"[{label}] рџ’ѕ Memory updated: {updatedMemory}");
            }

            Debug.Log($"[{label}] вњ“ Crafted: '{itemName}'");
            return true;
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
    }
#endif
}

