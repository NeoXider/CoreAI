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
                return System.Array.Empty<ChatMessage>();
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
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                //     MemoryStore (   )
                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

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

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 1");

                    LogAfterModelCall("craft 1", sink, store);
                    if (!ExtractCraftInfo(sink, store, craftedNames, ref memoryAccum, "craft 1", 1, ing1, ing2))
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

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 2");

                    LogAfterModelCall("craft 2", sink, store);
                    if (!ExtractCraftInfo(sink, store, craftedNames, ref memoryAccum, "craft 2", 2, ing1, ing2))
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

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 3");

                    LogAfterModelCall("craft 3", sink, store);
                    if (!ExtractCraftInfo(sink, store, craftedNames, ref memoryAccum, "craft 3", 3, ing1, ing2))
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

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "craft 4");

                    LogAfterModelCall("craft 4 (determinism)", sink, store);

                    //  :      #2
                    string craft2Name = craftedNames[1]; // Steel+Hardwood   #2
                    if (sink.Items.Count > 0)
                    {
                        string craft4Name = CraftingMemoryItemNameExtractor.ExtractName(sink.Items[0].JsonPayload);
                        Debug.Log(
                            $"[CraftingMemory.OpenAI] DETERMINISM CHECK: Craft #2 was '{craft2Name}', Craft #4 is '{craft4Name ?? "unknown"}'");

                        bool isDeterministic = !string.IsNullOrEmpty(craft4Name) &&
                                               craft4Name.ToLowerInvariant() == craft2Name.ToLowerInvariant();

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
                    }

                    if (!ExtractCraftInfo(sink, store, craftedNames, ref memoryAccum, "craft 4", 4, ing1, ing2))
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
                Debug.Log($"[CraftingMemory.OpenAI] Determinism: Craft#2='{craft2Final}' vs Craft#4='{craft4Final}' " +
                          $" {(craft2Final.ToLowerInvariant() == craft4Final.ToLowerInvariant() ? " SAME" : " DIFFERENT")}");
                Debug.Log($"[CraftingMemory.OpenAI] Canonical memory for prompts:\n{memoryAccum}");

                Assert.AreEqual(craft2Final.ToLowerInvariant(), craft4Final.ToLowerInvariant(),
                    $"Determinism failed: craft #4 must repeat craft #2 name. Craft2='{craft2Final}' Craft4='{craft4Final}'");
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
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                // =====  1 =====
                string prompt1 = BuildCraftPrompt(1,
                    "Steel Ingot (metal, hardness:80, magic:10, rarity:2)",
                    "Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)",
                    "");

                LogBeforeModelCall("CRAFT 1: Steel + Fire Crystal", prompt1, store);

                ListSink sink1 = new();
                AiOrchestrator orch1 = CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink1);

                Task t1 = orch1.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.CoreMechanic,
                    Hint = prompt1
                });

                yield return PlayModeTestAwait.WaitTask(t1, 300f, "craft 1");

                LogAfterModelCall("craft 1", sink1, store);

                if (sink1.Items.Count == 0)
                {
                    Assert.Fail("Craft 1 produced no output");
                    yield break;
                }

                string firstPayload = sink1.Items[0].JsonPayload;
                string firstName = CraftingMemoryItemNameExtractor.ExtractName(firstPayload);
                Debug.Log($"[CraftingMemory.OpenAI] Extracted Craft 1 name: '{firstName ?? "unknown"}'");

                // =====  2 (   + ) =====
                string memoryHint = "";
                if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState st1) &&
                    !string.IsNullOrWhiteSpace(st1.Memory))
                {
                    memoryHint = st1.Memory;
                }

                string prompt2 = BuildCraftPrompt(2,
                    "Steel Ingot (metal, hardness:80, magic:10, rarity:2)",
                    "Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)",
                    memoryHint);

                LogBeforeModelCall("CRAFT 2: Same ingredients, check memory", prompt2, store);

                ListSink sink2 = new();
                AiOrchestrator orch2 = CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink2);

                Task t2 = orch2.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.CoreMechanic,
                    Hint = prompt2
                });

                yield return PlayModeTestAwait.WaitTask(t2, 300f, "craft 2");

                LogAfterModelCall("craft 2", sink2, store);

                if (sink2.Items.Count == 0)
                {
                    Assert.Fail("Craft 2 produced no output");
                    yield break;
                }

                string secondPayload = sink2.Items[0].JsonPayload;
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
                    new System.Action<string>(code =>
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
                new NullAiOrchestrationMetrics(), UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());
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

            string instructions = "OUTPUT FORMAT:\n" +
                                  "1. First, call the memory tool to save this craft:\n" +
                                  "   ```json\n" +
                                  "   {\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Previous crafts: <list all crafts including this one>\"}}\n" +
                                  "   ```\n\n" +
                                  "2. Then, call the execute_lua tool to create the item:\n" +
                                  "   ```json\n" +
                                  "   {\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('YourWeaponName', 'weapon', quality)\\nreport('crafted YourWeaponName')\"}}\n" +
                                  "   ```";

            return header + ingredients + memorySection + instructions;
        }

        private static string BuildDeterministicCraftPrompt(int craftNumber, string ingredient1, string ingredient2,
            string previousCrafts)
        {
            string header = $"You are crafting a weapon. This is craft #{craftNumber}.\n\n";
            string ingredients = $"Ingredients:\n- {ingredient1}\n- {ingredient2}\n\n";
            string memorySection = string.IsNullOrEmpty(previousCrafts)
                ? "This is your first craft.\n\n"
                : $"YOUR MEMORY (ALL previous crafts):\n{previousCrafts}\n\n";

            string instructions = "IMPORTANT: These EXACT ingredients were used before (see memory above).\n" +
                                  "You MUST craft the EXACT SAME item as before  use the SAME name and properties.\n" +
                                  "This tests deterministic behavior: same input = same output.\n\n" +
                                  "OUTPUT FORMAT:\n" +
                                  "1. Call the memory tool:\n" +
                                  "   ```json\n" +
                                  "   {\"name\": \"memory\", \"arguments\": {\"action\": \"write\", \"content\": \"Previous crafts: <update list>\"}}\n" +
                                  "   ```\n\n" +
                                  "2. Call the execute_lua tool:\n" +
                                  "   ```json\n" +
                                  "   {\"name\": \"execute_lua\", \"arguments\": {\"code\": \"create_item('SameNameAsBefore', 'weapon', quality)\\nreport('crafted SameNameAsBefore')\"}}\n" +
                                  "   ```";

            return header + ingredients + memorySection + instructions;
        }

        #region Logging Helpers

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
                Debug.Log($"[CraftingMemory.OpenAI]   MEMORY: (empty  first craft)");
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
                Debug.Log($"[CraftingMemory.OpenAI]   MEMORY: (not written by model)");
            }

            Debug.Log($"[CraftingMemory.OpenAI] ");
        }

        private static bool ExtractCraftInfo(
            ListSink sink,
            InMemoryStore store,
            List<string> craftedNames,
            ref string memoryAccum,
            string label,
            int craftNumber,
            string ingredient1Short,
            string ingredient2Short)
        {
            if (sink.Items.Count == 0)
            {
                Debug.LogWarning($"[{label}]  No command produced  test cannot continue");
                return false;
            }

            string payload = sink.Items[0].JsonPayload;

            //   
            string itemName = CraftingMemoryItemNameExtractor.ExtractName(payload);
            if (string.IsNullOrEmpty(itemName))
            {
                Debug.LogWarning($"[{label}]  Could not extract item name from payload");
                itemName = $"unknown_{craftedNames.Count + 1}";
            }

            craftedNames.Add(itemName);
            memoryAccum = BuildCanonicalMemory(memoryAccum, craftNumber, itemName, ingredient1Short, ingredient2Short);

            //  
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState existing))
            {
                store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState
                {
                    Memory = existing.Memory + $" | New: {itemName}"
                });
            }

            Debug.Log($"[{label}]  Crafted: '{itemName}'");
            return true;
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
            "quality", "weapon", "memory", "item" // line "- Weapon created" () vs    
        };

        private static readonly Regex[] Patterns =
        {
            // Lua: create_item('Name', ...)
            new("create_item\\s*\\(\\s*'([^']+)'"),
            new("create_item\\s*\\(\\s*\"([^\"]+)\""),
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
            // JSON: "name": "..."
            new("\"name\"\\s*:\\s*\"([^\"]+)\""),
            new("Name\\s*=\\s*\"([^\"]+)\""),
            // " crafted with quality" must NOT match "with" as the name  (?!with\b)
            new("\\bcrafted\\s+(?!with\\b)\\s*\\*{0,2}([A-Za-z][A-Za-z0-9_']*(?:\\s+[A-Za-z][A-Za-z0-9_']*)*)\\*{0,2}", RegexOptions.IgnoreCase),
            // Freeform: "X created" (PascalCase multi-part)
            new("\\*{0,2}([A-Z][A-Za-z]{2,}(?:[A-Z][a-z]+)+)\\*{0,2}\\s+(?:created|crafted|forged)", RegexOptions.IgnoreCase),
            // Markdown bold: **WeaponName** (one word, after higher-priority patterns)
            new("\\*\\*([A-Z][A-Za-z0-9_]{3,})\\*\\*"),
        };

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

