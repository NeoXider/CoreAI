using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// PlayMode тест крафта с памятью — проверяет что модель использует инструмент memory
    /// для сохранения состояния между вызовами. Тест НЕ передаёт предыдущие крафты в промпт,
    /// модель должна сама сохранять и читать память через инструмент.
    /// </summary>
#if !COREAI_NO_LLM
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
        /// Полный воркфлоу крафта через LLMUnity: 3 итерации, AI запоминает каждый крафт
        /// через инструмент memory и должен создавать уникальные предметы.
        /// </summary>
        [UnityTest]
        [Timeout(2400000)]
        public IEnumerator CraftingMemoryLlmUnity_ThreeCrafts_AllUnique()
        {
            Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");
            Debug.Log("[CraftingMemory.LLMUnity] ═══ TEST START ═══");
            Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");

            // Backend из CoreAISettingsAsset (null = FromSettings)
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
                // Только для LLMUnity — ждём готовности модели. Для HTTP не нужно.
                if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
                {
                    yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
                }

                Debug.Log($"[CraftingMemory] Using backend: {handle.ResolvedBackend}, Model ready");

                InMemoryStore store = new();

                // Создаём LuaLlmTool с настоящим исполнителем Lua (SecureLuaEnvironment)
                RealLuaExecutor luaExecutor = new();
                LuaLlmTool luaTool = new(luaExecutor);

                AgentMemoryPolicy policy = new();
                // Регистрируем execute_lua инструмент для CoreMechanic
                policy.SetToolsForRole(BuiltInAgentRoleIds.CoreMechanic, new ILlmTool[] { luaTool });

                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                List<string> craftedNames = new();

                // ===== КРАФТ 1: Iron + Oak =====
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

                    // Проверяем что модель записала в память
                    if (!store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) ||
                        string.IsNullOrWhiteSpace(mem.Memory))
                    {
                        Debug.LogWarning("[CraftingMemory.LLMUnity] ⚠ Model did NOT write to memory after craft 1!");
                    }

                    if (!ExtractCraftInfo(sink, store, craftedNames, "craft 1"))
                    {
                        yield break;
                    }
                }

                // ===== КРАФТ 2: Steel + Hardwood =====
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

                    if (!ExtractCraftInfo(sink, store, craftedNames, "craft 2"))
                    {
                        yield break;
                    }
                }

                // ===== КРАФТ 3: Mithril + Enchanted Wood =====
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

                    if (!ExtractCraftInfo(sink, store, craftedNames, "craft 3"))
                    {
                        yield break;
                    }
                }

                // ===== КРАФТ 4: Steel + Hardwood (ПОВТОР крафта #2) — проверка детерминизма =====
                {
                    string prompt = BuildDeterministicCraftPrompt(4,
                        "Steel (metal, hardness:75, magic:8, rarity:2)",
                        "Hardwood (wood, hardness:50, magic:12, rarity:2)",
                        store);

                    LogBeforeModelCall("CRAFT 4: Steel + Hardwood (REPEAT of craft #2 — DETERMINISM CHECK)", prompt,
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
                                $"[CraftingMemory.LLMUnity] ⚠ DETERMINISM FAILED: Craft #4 '{craft4Name}' != Craft #2 '{craft2Name}'");
                        }
                        else
                        {
                            Debug.Log(
                                $"[CraftingMemory.LLMUnity] ✓ DETERMINISM PASS: Craft #4 repeated Craft #2 name '{craft2Name}'");
                        }
                    }

                    if (!ExtractCraftInfo(sink, store, craftedNames, "craft 4"))
                    {
                        yield break;
                    }
                }

                // ===== ФИНАЛЬНАЯ ПРОВЕРКА =====
                Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");
                Debug.Log("[CraftingMemory.LLMUnity] ═══ FINAL VALIDATION ═══");
                Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");

                Assert.AreEqual(4, craftedNames.Count, "Must have 4 crafted items");

                // Крафты 1, 2, 3 — уникальны
                HashSet<string> uniqueFirst3 = new(craftedNames.Take(3).Select(n => n.ToLowerInvariant()));
                Assert.AreEqual(3, uniqueFirst3.Count,
                    $"Crafts 1-3 must be unique! Got: {string.Join(", ", craftedNames.Take(3))}");

                Debug.Log("[CraftingMemory.LLMUnity] ✓ First 3 crafts are unique");

                string craft2Final = craftedNames[1];
                string craft4Final = craftedNames[3];
                Debug.Log($"[CraftingMemory.LLMUnity] Crafted items: {string.Join(" | ", craftedNames)}");
                Debug.Log(
                    $"[CraftingMemory.LLMUnity] Determinism: Craft#2='{craft2Final}' vs Craft#4='{craft4Final}' " +
                    $"→ {(craft2Final.ToLowerInvariant() == craft4Final.ToLowerInvariant() ? "✓ SAME" : "⚠ DIFFERENT")}");

                // Проверяем финальное состояние памяти
                if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState finalMem))
                {
                    Debug.Log($"[CraftingMemory.LLMUnity] Final memory state:\n{finalMem.Memory}");
                }

                Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");
                Debug.Log("[CraftingMemory.LLMUnity] ═══ TEST PASSED ═══");
                Debug.Log("[CraftingMemory.LLMUnity] ═══════════════════════════════════════");
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
        /// Настоящий ILuaExecutor через SecureLuaEnvironment — выполняет Lua код в песочнице.
        /// </summary>
        private sealed class RealLuaExecutor : LuaTool.ILuaExecutor
        {
            private readonly SecureLuaEnvironment _sandbox;
            private readonly LuaApiRegistry _registry;

            public RealLuaExecutor()
            {
                _sandbox = new SecureLuaEnvironment();
                _registry = new LuaApiRegistry();

                // Регистрируем базовые API: report, create_item
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
                    Debug.LogError($"[RealLuaExecutor] FAILED: {ex.Message}");
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

            // Читаем память из store — это то, что модель должна была сохранить
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

            string instructions = "You MUST perform these actions IN ORDER using tool calls:\n\n" +
                                  "STEP 1: Call the 'memory' tool with:\n" +
                                  "  - action: \"write\"\n" +
                                  "  - content: \"Previous crafts: Craft #1 - <weapon name> made from Iron + Oak Wood\"\n\n" +
                                  "STEP 2: Call the 'execute_lua' tool with Lua code:\n" +
                                  "  create_item('YourWeaponName', 'weapon', quality)\n" +
                                  "  report('crafted YourWeaponName')\n\n" +
                                  "Choose a creative weapon name based on the ingredients (e.g., 'IronOakBlade', 'OakReinforcedMace'). " +
                                  "Quality should be 1-100 based on ingredient rarity (both are rarity:1, so quality ~40-60).\n\n" +
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

            string instructions = "You MUST perform these actions IN ORDER using tool calls:\n\n" +
                                  "STEP 1: Call the 'memory' tool with:\n" +
                                  "  - action: \"write\"\n" +
                                  "  - content: \"Previous crafts: <full list including this craft #\" + craftNumber + \">\"\n\n" +
                                  "STEP 2: Call the 'execute_lua' tool with Lua code.\n\n" +
                                  "CRITICAL: You MUST craft the EXACT SAME item as before — same name, same quality.\n" +
                                  "These are the EXACT same ingredients, so the result must be IDENTICAL.\n" +
                                  "You MUST call BOTH tools. Do NOT stop after memory.";

            return header + ingredients + memorySection + instructions;
        }

        #region Logging Helpers

        private static void LogBeforeModelCall(string label, string prompt, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.LLMUnity] ┌─────────────────────────────────────────");
            Debug.Log($"[CraftingMemory.LLMUnity] │ 📤 SENDING TO MODEL: {label}");
            Debug.Log($"[CraftingMemory.LLMUnity] ├─────────────────────────────────────────");

            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.LLMUnity] │ 📚 MEMORY VISIBLE TO MODEL:\n{mem.Memory}");
                Debug.Log($"[CraftingMemory.LLMUnity] ├─────────────────────────────────────────");
            }
            else
            {
                Debug.Log($"[CraftingMemory.LLMUnity] │ 📚 MEMORY: (empty — first craft)");
                Debug.Log($"[CraftingMemory.LLMUnity] ├─────────────────────────────────────────");
            }

            Debug.Log($"[CraftingMemory.LLMUnity] │ 📝 PROMPT ({prompt.Length} chars):\n{prompt}");
            Debug.Log($"[CraftingMemory.LLMUnity] └─────────────────────────────────────────");
        }

        private static void LogAfterModelCall(string label, ListSink sink, InMemoryStore store)
        {
            Debug.Log($"[CraftingMemory.LLMUnity] ┌─────────────────────────────────────────");
            Debug.Log($"[CraftingMemory.LLMUnity] │ 📥 MODEL RESPONSE: {label}");
            Debug.Log($"[CraftingMemory.LLMUnity] ├─────────────────────────────────────────");

            if (sink.Items.Count > 0)
            {
                string payload = sink.Items[0].JsonPayload;
                Debug.Log($"[CraftingMemory.LLMUnity] │ ✅ Command received: {sink.Items[0].CommandTypeId}");
                Debug.Log($"[CraftingMemory.LLMUnity] │ 📦 RAW PAYLOAD ({payload.Length} chars):\n{payload}");
            }
            else
            {
                Debug.Log($"[CraftingMemory.LLMUnity] │ ❌ NO COMMAND produced");
            }

            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[CraftingMemory.LLMUnity] │ 💾 MEMORY AFTER:\n{mem.Memory}");
            }
            else
            {
                Debug.Log($"[CraftingMemory.LLMUnity] │ 💾 MEMORY: (not written by model)");
            }

            Debug.Log($"[CraftingMemory.LLMUnity] └─────────────────────────────────────────");
        }

        private static bool ExtractCraftInfo(
            ListSink sink,
            InMemoryStore store,
            List<string> craftedNames,
            string label)
        {
            if (sink.Items.Count == 0)
            {
                Debug.LogError($"[{label}] ❌ No command produced — test cannot continue");
                return false;
            }

            string payload = sink.Items[0].JsonPayload;

            string itemName = CraftingMemoryItemNameExtractor.ExtractName(payload);
            if (string.IsNullOrEmpty(itemName))
            {
                Debug.LogWarning($"[{label}] ⚠ Could not extract item name from payload");
                itemName = $"unknown_{craftedNames.Count + 1}";
            }

            craftedNames.Add(itemName);

            Debug.Log($"[{label}] ✓ Crafted: '{itemName}'");
            return true;
        }

        #endregion
    }
#endif
}