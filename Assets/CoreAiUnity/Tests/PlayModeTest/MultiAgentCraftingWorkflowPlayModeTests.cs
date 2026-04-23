using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// РџРѕР»РЅС‹Р№ РІРѕСЂРєС„Р»РѕСѓ РєСЂР°С„С‚Р° СЃ РќР•РЎРљРћР›Р¬РљРРњР РђР“Р•РќРўРђРњР:
    /// Creator (РґРёР·Р°Р№РЅ) в†’ CoreMechanicAI (СЂР°СЃС‡С‘С‚) в†’ Programmer (Lua РєРѕРґ).
    /// РљР°Р¶РґС‹Р№ Р°РіРµРЅС‚ РёСЃРїРѕР»СЊР·СѓРµС‚ СЃРІРѕСЋ РёР·РѕР»РёСЂРѕРІР°РЅРЅСѓСЋ РїР°РјСЏС‚СЊ, РѕРґРЅСѓ Рё С‚Сѓ Р¶Рµ РјРѕРґРµР»СЊ.
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
    public sealed class MultiAgentCraftingWorkflowPlayModeTests
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
                Items.Add(command);
            }
        }

        /// <summary>
        /// РџРѕР»РЅС‹Р№ РІРѕСЂРєС„Р»РѕСѓ: Creator в†’ CoreMechanicAI в†’ Programmer
        /// </summary>
        [UnityTest]
        [Timeout(1800000)]
        public IEnumerator MultiAgent_CreatorThenMechanicThenProgrammer_CompleteWorkflow()
        {
            Debug.Log("[MultiAgent] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
            Debug.Log("[MultiAgent] в•ђв•ђв•ђ TEST START: Creator в†’ CoreMechanic в†’ Programmer в•ђв•ђв•ђ");
            Debug.Log("[MultiAgent] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");

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
                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                // РћР±РµСЂРЅСѓС‚СЊ РєР»РёРµРЅС‚ СЃ РїСЂР°РІРёР»СЊРЅС‹Рј MemoryStore
                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                // ===== РЁРђР“ 1: Creator СЂРµС€Р°РµС‚ С‡С‚Рѕ РєСЂР°С„С‚РёС‚СЊ =====
                {
                    Debug.Log("[MultiAgent] в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");
                    Debug.Log("[MultiAgent] в”‚ РЁРђР“ 1: Creator вЂ” РґРёР·Р°Р№РЅ РєСЂР°С„С‚Р°");
                    Debug.Log("[MultiAgent] в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

                    LogAgentMemory(store, "Creator");

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Creator,
                        Hint = "Design a crafting recipe for a weapon made from these ingredients:\n" +
                               "- Iron (metal, hardness:60, magic:5, rarity:1)\n" +
                               "- Fire Crystal (crystal, hardness:30, magic:85, rarity:4, fire_damage:25)\n\n" +
                               "STEP 1: You must call the 'memory' tool (action: 'write', content: 'Design: Iron+Fire Crystal -> weapon, damage ~45, fire ~15').\n" +
                               "STEP 2: Once the memory tool succeeds, stop calling tools and output a raw JSON response: {\"item_type\": \"weapon\", \"estimated_damage\": 45, \"estimated_fire_damage\": 15, \"quality\": 1}"
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "creator design");

                    LogAgentResponse("creator", sink);
                    LogAgentMemory(store, "Creator");

                    // РџСЂРѕРІРµСЂСЏРµРј С‡С‚Рѕ Creator Р·Р°РїРёСЃР°Р» РІ РїР°РјСЏС‚СЊ
                    Assert.IsTrue(
                        store.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState creatorMem) &&
                        !string.IsNullOrWhiteSpace(creatorMem.Memory),
                        "Creator did not write to memory");

                    Debug.Log($"[MultiAgent] вњ“ Creator memory: {creatorMem.Memory}");
                }

                // ===== РЁРђР“ 2: CoreMechanicAI СЃС‡РёС‚Р°РµС‚ СЂРµР·СѓР»СЊС‚Р°С‚ =====
                {
                    Debug.Log("[MultiAgent] в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");
                    Debug.Log("[MultiAgent] в”‚ РЁРђР“ 2: CoreMechanicAI вЂ” СЂР°СЃС‡С‘С‚ РјРµС…Р°РЅРёРєРё");
                    Debug.Log("[MultiAgent] в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

                    LogAgentMemory(store, "CoreMechanicAI");

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = "Calculate craft result for Iron + Fire Crystal.\n" +
                               "CRITICAL INSTRUCTION:\n" +
                               "1. You MUST FIRST call the 'memory' tool (action: 'write', content: 'Craft#1: weapon damage:45').\n" +
                               "2. ONLY AFTER the tool succeeds, output JSON: {\"item_name\": \"Frostblade\", \"damage\": 45}"
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "mechanic calculation");

                    LogAgentResponse("mechanic", sink);
                    LogAgentMemory(store, "CoreMechanicAI");

                    Assert.IsTrue(
                        store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mechanicMem) &&
                        !string.IsNullOrWhiteSpace(mechanicMem.Memory),
                        "CoreMechanicAI did not write to memory");

                    string mechanicMemory = mechanicMem.Memory;
                    Debug.Log($"[MultiAgent] вњ“ CoreMechanicAI memory: {mechanicMemory}");

                    // РР·РІР»РµРєР°РµРј РёРјСЏ РїСЂРµРґРјРµС‚Р°
                    string itemName =
                        CraftingMemoryItemNameExtractor.ExtractName(sink.Items.Count > 0
                            ? sink.Items[0].JsonPayload
                            : "");
                    if (string.IsNullOrEmpty(itemName))
                    {
                        // РџС‹С‚Р°РµРјСЃСЏ РёР·РІР»РµС‡СЊ РёР· РїР°РјСЏС‚Рё
                        Match match = Regex.Match(mechanicMemory, @"Craft#1:\s*(\w+)");
                        if (match.Success)
                        {
                            itemName = match.Groups[1].Value;
                        }
                    }

                    if (!string.IsNullOrEmpty(itemName))
                    {
                        Debug.Log($"[MultiAgent] вњ“ Item name from CoreMechanicAI: '{itemName}'");
                    }
                }

                // ===== РЁРђР“ 3: Programmer РіРµРЅРµСЂРёСЂСѓРµС‚ Lua РєРѕРґ =====
                {
                    Debug.Log("[MultiAgent] в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");
                    Debug.Log("[MultiAgent] в”‚ РЁРђР“ 3: Programmer вЂ” РіРµРЅРµСЂР°С†РёСЏ Lua");
                    Debug.Log("[MultiAgent] в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

                    LogAgentMemory(store, "Programmer");

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Programmer,
                        Hint = "Generate Lua code for a weapon.\n" +
                               "CRITICAL INSTRUCTION:\n" +
                               "1. You MUST FIRST call the 'memory' tool (action: 'write', content: 'Programmer wrote Lua script').\n" +
                               "2. ONLY THEN call the 'execute_lua' tool to run the code."
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "programmer lua");

                    LogAgentResponse("programmer", sink);
                    LogAgentMemory(store, "Programmer");
                }

                // ===== РЁРђР“ 4: CoreMechanicAI вЂ” РїРѕРІС‚РѕСЂРЅС‹Р№ РєСЂР°С„С‚ (РґРµС‚РµСЂРјРёРЅРёР·Рј) =====
                {
                    Debug.Log("[MultiAgent] в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");
                    Debug.Log("[MultiAgent] в”‚ РЁРђР“ 4: CoreMechanicAI вЂ” РїРѕРІС‚РѕСЂРЅС‹Р№ РєСЂР°С„С‚ (РґРµС‚РµСЂРјРёРЅРёР·Рј)");
                    Debug.Log("[MultiAgent] в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ");

                    // РЎРѕС…СЂР°РЅСЏРµРј РёРјСЏ РёР· РїРµСЂРІРѕРіРѕ РєСЂР°С„С‚Р°
                    string craft1Memory = "";
                    if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem1))
                    {
                        craft1Memory = mem1.Memory;
                        Debug.Log($"[MultiAgent] Previous craft memory: {craft1Memory}");
                    }

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = "Calculate craft result for Iron + Fire Crystal.\n" +
                               $"YOUR PREVIOUS CRAFT MEMORY: {craft1Memory}\n\n" +
                               "CRITICAL INSTRUCTION:\n" +
                               "1. You MUST FIRST call the 'memory' tool (action: 'write', content: 'Craft#2: weapon').\n" +
                               "2. ONLY THEN output EXACT SAME JSON as your previous craft."
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "mechanic repeat");

                    LogAgentResponse("mechanic repeat", sink);
                    LogAgentMemory(store, "CoreMechanicAI");

                    // РџСЂРѕРІРµСЂСЏРµРј С‡С‚Рѕ РїР°РјСЏС‚СЊ РѕР±РЅРѕРІР»РµРЅР°
                    if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem2))
                    {
                        Debug.Log($"[MultiAgent] Final CoreMechanicAI memory:\n{mem2.Memory}");
                    }
                }

                // ===== Р¤РРќРђР›Р¬РќРђРЇ РџР РћР’Р•Р РљРђ =====
                Debug.Log("[MultiAgent] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                Debug.Log("[MultiAgent] в•ђв•ђв•ђ FINAL VALIDATION в•ђв•ђв•ђ");
                Debug.Log("[MultiAgent] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");

                // РџСЂРѕРІРµСЂСЏРµРј РёР·РѕР»СЏС†РёСЋ РїР°РјСЏС‚Рё
                Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState creatorState));
                Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mechanicState));
                Assert.IsTrue(store.TryLoad(BuiltInAgentRoleIds.Programmer, out AgentMemoryState programmerState));

                Debug.Log($"[MultiAgent] Creator memory:      {creatorState.Memory}");
                Debug.Log($"[MultiAgent] CoreMechanic memory: {mechanicState.Memory}");
                Debug.Log($"[MultiAgent] Programmer memory:  {programmerState.Memory}");

                // РџР°РјСЏС‚СЊ СЂР°Р·РЅС‹С… Р°РіРµРЅС‚РѕРІ РќР• РґРѕР»Р¶РЅР° СЃРѕРІРїР°РґР°С‚СЊ
                Assert.AreNotEqual(creatorState.Memory, mechanicState.Memory,
                    "Creator and CoreMechanicAI must have DIFFERENT memory");
                Assert.AreNotEqual(mechanicState.Memory, programmerState.Memory,
                    "Mechanic and Programmer must have DIFFERENT memory");

                Debug.Log("[MultiAgent] вњ“ Memory isolation verified");
                Debug.Log("[MultiAgent] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
                Debug.Log("[MultiAgent] в•ђв•ђв•ђ TEST PASSED в•ђв•ђв•ђ");
                Debug.Log("[MultiAgent] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>
        /// РЈРїСЂРѕС‰С‘РЅРЅС‹Р№ С‚РµСЃС‚: С‚РѕР»СЊРєРѕ Creator в†’ CoreMechanicAI (Р±РµР· Programmer)
        /// Р‘С‹СЃС‚СЂРµРµ РґР»СЏ РѕС‚Р»Р°РґРєРё.
        /// </summary>
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator MultiAgent_CreatorThenMechanic_QuickWorkflow()
        {
            Debug.Log("[MultiAgent.Quick] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");
            Debug.Log("[MultiAgent.Quick] в•ђв•ђв•ђ TEST START: Creator в†’ CoreMechanic в•ђв•ђв•ђ");
            Debug.Log("[MultiAgent.Quick] в•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђв•ђ");

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
                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                SessionTelemetryCollector telemetry = new();
                AiPromptComposer composer = new(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore());

                ILlmClient clientWithMemory = handle.WrapWithMemoryStore(store);

                // ===== CREATOR =====
                {
                    Debug.Log("[MultiAgent.Quick] === CREATOR: Design craft ===");
                    LogAgentMemory(store, "Creator");

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.Creator,
                        Hint =
                            "Design a weapon from: Iron (hardness:60, rarity:1) + Fire Crystal (magic:85, rarity:4).\n" +
                            "STEP 1: Call 'memory' tool (action: 'write', content: 'Design: Iron+Fire Crystal -> weapon').\n" +
                            "STEP 2: Output JSON response."
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "creator");
                    LogAgentResponse("creator", sink);
                    LogAgentMemory(store, "Creator");

                    Assert.IsTrue(
                        store.TryLoad(BuiltInAgentRoleIds.Creator, out AgentMemoryState _creatorMemQuick) &&
                        !string.IsNullOrWhiteSpace(_creatorMemQuick.Memory),
                        "Creator did not write memory");
                }

                // ===== COREMECHANIC =====
                {
                    Debug.Log("[MultiAgent.Quick] === COREMECHANIC: Calculate result ===");
                    LogAgentMemory(store, "CoreMechanicAI");

                    ListSink sink = new();
                    AiOrchestrator orch =
                        CreateOrchestrator(clientWithMemory, store, policy, telemetry, composer, sink);

                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = "Calculate weapon from: Iron (hardness:60) + Fire Crystal (magic:85).\n" +
                               "STEP 1: Call 'memory' tool (action: 'write', content: 'Craft#1: weapon damage:20 fire:10').\n" +
                               "STEP 2: Output JSON with item_name, damage, fire_damage."
                    });

                    yield return PlayModeTestAwait.WaitTask(t, 300f, "mechanic");
                    LogAgentResponse("mechanic", sink);
                    LogAgentMemory(store, "CoreMechanicAI");

                    Assert.IsTrue(
                        store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState _mechanicMemQuick) &&
                        !string.IsNullOrWhiteSpace(_mechanicMemQuick.Memory),
                        "CoreMechanicAI did not write memory");
                }

                // ===== РџР РћР’Р•Р РљРђ РР—РћР›РЇР¦РР =====
                Debug.Log("[MultiAgent.Quick] в•ђв•ђв•ђ MEMORY ISOLATION CHECK в•ђв•ђв•ђ");

                AgentMemoryState creatorMem = store.States[BuiltInAgentRoleIds.Creator];
                AgentMemoryState mechanicMem = store.States[BuiltInAgentRoleIds.CoreMechanic];

                Debug.Log($"[MultiAgent.Quick] Creator memory:      {creatorMem.Memory}");
                Debug.Log($"[MultiAgent.Quick] CoreMechanic memory: {mechanicMem.Memory}");

                Assert.AreNotEqual(creatorMem.Memory, mechanicMem.Memory,
                    "Agents must have isolated memory");

                Debug.Log("[MultiAgent.Quick] вњ“ Memory isolation verified");
                Debug.Log("[MultiAgent.Quick] в•ђв•ђв•ђ TEST PASSED в•ђв•ђв•ђ");
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

        #region Logging

        private static void LogAgentMemory(InMemoryStore store, string roleId)
        {
            if (store.TryLoad(roleId, out AgentMemoryState mem) && !string.IsNullOrWhiteSpace(mem.Memory))
            {
                Debug.Log($"[MultiAgent] рџ“љ {roleId} MEMORY: {mem.Memory}");
            }
            else
            {
                Debug.Log($"[MultiAgent] рџ“љ {roleId} MEMORY: (empty)");
            }
        }

        private static void LogAgentResponse(string label, ListSink sink)
        {
            if (sink.Items.Count > 0)
            {
                string payload = sink.Items[0].JsonPayload;
                Debug.Log($"[MultiAgent] рџ“Ґ {label} RESPONSE ({payload.Length} chars):\n{payload}");
            }
            else
            {
                Debug.Log($"[MultiAgent] вљ  {label}: No response");
            }
        }

        #endregion
    }
#endif
}
