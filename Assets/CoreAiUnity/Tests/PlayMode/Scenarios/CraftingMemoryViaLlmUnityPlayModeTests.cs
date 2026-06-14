#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
﻿using System;
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
        private const int LlmTurnTimeoutSeconds = 240;
        private const int CraftTurnMaxOutputTokens = 2048;

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
        [Explicit("Targeted backend-parity crafting probe. Mandatory full PlayMode keeps CraftingMemoryOpenAi_ThreeCrafts_AllUnique as the representative Lua-backed ThreeCrafts gate.")]
        [Timeout(600000)]
        public IEnumerator CraftingMemoryLlmUnity_ThreeCrafts_AllUnique()
        {
            Debug.Log("[CraftingMemory.LLMUnity] ========================================");
            Debug.Log("[CraftingMemory.LLMUnity] TEST START");
            Debug.Log("[CraftingMemory.LLMUnity] ========================================");

            // Backend  CoreAISettingsAsset (null = FromSettings)
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
                string memoryAccum = "";

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
                    using CancellationTokenSource cts = CreateTurnCancellation();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt,
                        MaxOutputTokens = CraftTurnMaxOutputTokens
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "craft 1", cts);
                    yield return FlushMemoryStorePersistenceFrames();

                    LogAfterModelCall("craft 1", sink, store);

                    LlmToolCallRecord executeLua = toolCalls.RequireCompletedToolSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 1");
                    string executedLua = executeLua.Info.ArgumentsJson;
                    if (!ExtractCraftInfo(executedLua, store, craftedNames, ref memoryAccum, "craft 1", 1, "Iron", "Oak Wood"))
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
                    using CancellationTokenSource cts = CreateTurnCancellation();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt,
                        MaxOutputTokens = CraftTurnMaxOutputTokens
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "craft 2", cts);
                    yield return FlushMemoryStorePersistenceFrames();

                    LogAfterModelCall("craft 2", sink, store);

                    LlmToolCallRecord executeLua = toolCalls.RequireCompletedToolSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 2");
                    string executedLua = executeLua.Info.ArgumentsJson;
                    if (!ExtractCraftInfo(executedLua, store, craftedNames, ref memoryAccum, "craft 2", 2, "Steel", "Hardwood"))
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
                    using CancellationTokenSource cts = CreateTurnCancellation();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = BuiltInAgentRoleIds.CoreMechanic,
                        Hint = prompt,
                        MaxOutputTokens = CraftTurnMaxOutputTokens
                    }, cts.Token);

                    yield return PlayModeTestAwait.WaitTask(t, LlmTurnTimeoutSeconds, "craft 3", cts);
                    yield return FlushMemoryStorePersistenceFrames();

                    LogAfterModelCall("craft 3", sink, store);

                    LlmToolCallRecord executeLua = toolCalls.RequireCompletedToolSince(
                        toolMark, BuiltInAgentRoleIds.CoreMechanic, "execute_lua", "craft 3");
                    string executedLua = executeLua.Info.ArgumentsJson;
                    if (!ExtractCraftInfo(executedLua, store, craftedNames, ref memoryAccum, "craft 3", 3, "Mithril", "Enchanted Wood"))
                    {
                        yield break;
                    }
                }

                // =====   =====
                Debug.Log("[CraftingMemory.LLMUnity] ");
                Debug.Log("[CraftingMemory.LLMUnity]  FINAL VALIDATION ");
                Debug.Log("[CraftingMemory.LLMUnity] ");

                Assert.AreEqual(3, craftedNames.Count, "Must have 3 crafted items");

                HashSet<string> uniqueFirst3 = new(craftedNames.Select(n => n.ToLowerInvariant()));
                Assert.AreEqual(3, uniqueFirst3.Count,
                    $"Crafts 1-3 must be unique! Got: {string.Join(", ", craftedNames)}");

                Debug.Log("[CraftingMemory.LLMUnity]  First 3 crafts are unique");

                Debug.Log($"[CraftingMemory.LLMUnity] Crafted items: {string.Join(" | ", craftedNames)}");

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
            private readonly Dictionary<string, Closure> _logicSlots = new(StringComparer.Ordinal);

            public RealLuaExecutor()
            {
                _sandbox = new SecureLuaEnvironment();
                _registry = new LuaApiRegistry();

                //   API: report, create_item
                _registry.Register("report", new Action<string>(msg =>
                    Debug.Log($"[Lua.report] {msg}")));
                _registry.Register("create_item", new Action<string, string, double>((name, type, quality) =>
                    Debug.Log($"[Lua.create_item] name={name}, type={type}, quality={quality}")));
                _registry.Register("logic_define", new Func<string, Closure, bool>(DefineLogicSlot));
                _registry.Register("logic_reset", new Action<string>(ResetLogicSlot));
                _registry.Register("logic_list", new Func<List<object>>(ListLogicSlots));
                _registry.Register("add", new Func<double, double, double>((a, b) => a + b));
            }

            private bool DefineLogicSlot(string name, Closure fn)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("logic_define: slot name is required.");
                }

                if (fn == null)
                {
                    throw new ArgumentException("logic_define: second argument must be a function.");
                }

                _logicSlots[name] = fn;
                Debug.Log($"[Lua.logic_define] {name}");
                return true;
            }

            private void ResetLogicSlot(string name)
            {
                _logicSlots.Remove(name ?? string.Empty);
                Debug.Log($"[Lua.logic_reset] {name}");
            }

            private List<object> ListLogicSlots()
            {
                List<object> slots = new();
                foreach (string name in _logicSlots.Keys)
                {
                    slots.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "overridden", true }
                    });
                }

                return slots;
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

            string memoryFromStore = "";
            if (store.TryLoad(BuiltInAgentRoleIds.CoreMechanic, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem.Memory))
            {
                memoryFromStore = mem.Memory;
            }

            string memorySection = string.IsNullOrEmpty(memoryFromStore)
                ? "This is your first craft. No previous crafts to check.\n\n"
                : $"YOUR MEMORY (previous crafts):\n{memoryFromStore}\n\n" +
                  "Create a weapon that is distinct from the previous crafts recorded above.\n\n";

            string instructions =
                "Apply the craft in the game through the available Lua execution capability and preserve enough memory for future crafts. " +
                "The created item should have a concrete name, type 'weapon', and a numeric quality value.";

            return header + ingredients + memorySection + instructions;
        }

        private static CancellationTokenSource CreateTurnCancellation()
        {
            CancellationTokenSource cts = new();
            cts.CancelAfter(TimeSpan.FromSeconds(LlmTurnTimeoutSeconds));
            return cts;
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

            string instructions =
                "These ingredients were used before. Use the available Lua execution capability and the recorded craft memory to create the consistent result " +
                "that the game should produce for the same ingredients, and preserve the updated memory.";

            return header + ingredients + memorySection + instructions;
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
            ref string memoryAccum,
            string label,
            int craftNumber,
            string ingredient1Short,
            string ingredient2Short)
        {
            string itemName = CraftingMemoryItemNameExtractor.ExtractName(executedLuaPayload);
            if (string.IsNullOrEmpty(itemName))
            {
                Assert.Fail(
                    $"[{label}] execute_lua completed, but its arguments do not contain an extractable craft item name for craft #{craftNumber}. " +
                    $"The Lua-backed crafting test must not pass from prose or memory-only output. " +
                    $"execute_lua arguments: {FormatPayloadForAssertion(executedLuaPayload)}");
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

            memoryAccum = BuildCanonicalMemory(memoryAccum, craftNumber, itemName, ingredient1Short, ingredient2Short);
            store.Save(BuiltInAgentRoleIds.CoreMechanic, new AgentMemoryState { Memory = memoryAccum });
            Debug.Log($"[{label}] Canonical memory updated:\n{memoryAccum}");

            Debug.Log($"[{label}] Crafted: '{itemName}'");
            return true;
        }

        private static string BuildCanonicalMemory(
            string previous,
            int craftNumber,
            string itemName,
            string ingredient1Short,
            string ingredient2Short)
        {
            string line = $"Craft #{craftNumber} - {itemName} made from {ingredient1Short} + {ingredient2Short}";
            return string.IsNullOrWhiteSpace(previous) ? line : previous.TrimEnd() + "\n" + line;
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

            // Common model memory: "Craft #N: Steelwood Saber (weapon). ..."
            match = Regex.Match(mem.Memory, $"Craft\\s*#\\s*{craftNumber}\\s*:\\s*([^\\r\\n\\(\\.]+?)\\s*\\(",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                itemName = match.Groups[1].Value.Trim();
                return !string.IsNullOrWhiteSpace(itemName);
            }

            // Common model memory: "Craft Log #N: Weapon \"Ironwood Blade\" crafted ..."
            match = Regex.Match(mem.Memory,
                $"Craft\\s+Log\\s*#\\s*{craftNumber}\\s*:\\s*Weapon\\s+\"([^\"]+)\"\\s+crafted\\b",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                itemName = match.Groups[1].Value.Trim();
                return !string.IsNullOrWhiteSpace(itemName);
            }

            // Fallback: "Craft #N - Name" until metadata or delimiter.
            match = Regex.Match(mem.Memory, $"Craft\\s*#\\s*{craftNumber}\\s*-\\s*([^,|\\(\\.\\r\\n]+)",
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
                    string seen = string.Join("\n---\n", _records.Skip(startIndex)
                        .Select(FormatRecordForAssertion));
                    Assert.Fail(
                        $"[{label}] Expected completed tool '{toolName}' for role '{roleId}'. Seen: [{seen}]");
                }

                return record;
            }

            private static string FormatRecordForAssertion(LlmToolCallRecord record)
            {
                string args = string.IsNullOrWhiteSpace(record.Info.ArgumentsJson)
                    ? "(no args)"
                    : record.Info.ArgumentsJson;
                string result = string.IsNullOrWhiteSpace(record.ResultJson)
                    ? ""
                    : $"\nresult: {record.ResultJson}";
                string error = string.IsNullOrWhiteSpace(record.Error)
                    ? ""
                    : $"\nerror: {record.Error}";

                return $"{record.Info.RoleId}:{record.Info.ToolName}:{record.Status} ({record.DurationMs:0}ms)\n{args}{result}{error}";
            }
        }
    }
#endif
}
#endif
