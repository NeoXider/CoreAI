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
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Self-service skill test: model must call read_skill to discover enchant_weapon,
    /// then call_skill_tool to execute it. The tool is NOT in the model's tool list —
    /// only read_skill and call_skill_tool are visible.
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
    public sealed class SkillSetToolDiscoveryPlayModeTests
    {
        private static bool _enchantWeaponCalled;
        private static string _enchantTarget;
        private static string _enchantType;

        private static void Reset()
        {
            _enchantWeaponCalled = false;
            _enchantTarget = null;
            _enchantType = null;
        }

        private static object EnchantWeapon(string weapon_name, string enchantment_type)
        {
            _enchantWeaponCalled = true;
            _enchantTarget = weapon_name;
            _enchantType = enchantment_type;
            Debug.Log($"[ToolDiscovery] ✨ enchant_weapon executed: weapon={weapon_name}, type={enchantment_type}");

            return new
            {
                success = true,
                weapon = weapon_name,
                enchantment = enchantment_type,
                bonus = "+15 fire damage",
                message = $"{weapon_name} now burns with fire!"
            };
        }

        private static object SearchMap(string location)
        {
            Debug.Log($"[ToolDiscovery] 🗺 search_map called: {location}");
            return new { found = "nothing" };
        }

        private sealed class CaptureLlm : ILlmClient
        {
            private readonly ILlmClient _inner;
            public int CallCount;
            public string LastContent;
            public bool LastOk;
            public string FirstSystemPrompt;

            public CaptureLlm(ILlmClient inner)
            {
                _inner = inner;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request, CancellationToken ct = default)
            {
                CallCount++;

                if (CallCount == 1)
                {
                    FirstSystemPrompt = request.SystemPrompt;
                    Debug.Log($"[ToolDiscovery] ╔══ SYSTEM PROMPT ({request.SystemPrompt?.Length} chars) ══");
                    Debug.Log($"[ToolDiscovery] ║ {request.SystemPrompt}");
                    Debug.Log($"[ToolDiscovery] ╚══════════════════════════════════════");

                    if (request.Tools != null)
                    {
                        Debug.Log($"[ToolDiscovery] 🔧 Tools sent to model ({request.Tools.Count}):");
                        foreach (ILlmTool tool in request.Tools)
                        {
                            Debug.Log($"[ToolDiscovery]    • {tool.Name}: {tool.Description}");
                        }
                    }
                }

                Debug.Log($"[ToolDiscovery] ── LLM call #{CallCount} ──");

                LlmCompletionResult result = await _inner.CompleteAsync(request, ct);
                LastOk = result is { Ok: true };
                LastContent = result?.Content;

                if (!string.IsNullOrEmpty(result?.Content))
                {
                    Debug.Log($"[ToolDiscovery] 💬 Model response: {result.Content}");
                }

                return result;
            }
        }

        private sealed class Sink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand c)
            {
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Model_ReadsSkill_ThenCallsSkillToolViaProxy()
        {
            Debug.Log("[ToolDiscovery] ═══════════════════════════════════════════");
            Debug.Log("[ToolDiscovery] Test: model discovers enchant_weapon via read_skill,");
            Debug.Log("[ToolDiscovery] then calls it via call_skill_tool proxy.");
            Debug.Log("[ToolDiscovery] Model sees ONLY: read_skill + call_skill_tool (2 tools).");
            Debug.Log("[ToolDiscovery] ═══════════════════════════════════════════");

            Reset();

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.1f, 120,
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);

                SkillSet enchantingSkill = new("Enchanting",
                    "Apply magical enchantments to weapons and armor",
                    "You are an enchantment master.\n" +
                    "When the player asks to enchant an item, call enchant_weapon with:\n" +
                    "- weapon_name: the name of the weapon\n" +
                    "- enchantment_type: the type of enchantment (fire, ice, lightning)\n" +
                    "Report the result to the player.",
                    new DelegateLlmTool("enchant_weapon",
                        "Apply enchantment to a weapon. Parameters: weapon_name (string), enchantment_type (string)",
                        new Func<string, string, object>(EnchantWeapon)));

                SkillSet explorationSkill = new("Exploration",
                    "Explore the map and find locations",
                    "Search for locations on the map.",
                    new DelegateLlmTool("search_map",
                        "Search the map",
                        new Func<string, object>(SearchMap)));

                const string roleId = "EnchantMaster";
                AgentConfig config = new AgentBuilder(roleId)
                    {
                        SuppressBuildWarnings = true
                    }
                    .WithSystemPrompt(
                        "You are a Game Master in a fantasy RPG.\n" +
                        "When the player asks you to do something, use the relevant available skill " +
                        "and its tools to complete the request.")
                    .WithSkill(enchantingSkill)
                    .WithSkill(explorationSkill)
                    .WithMode(AgentMode.ToolsAndChat)
                    .Build();

                InMemoryStore store = new();
                AgentMemoryPolicy policy = new();
                config.ApplyToPolicy(policy);

                CaptureLlm cap = new(handle.WrapWithMemoryStore(store));
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

                AiOrchestrator orch = new(
                    new SoloAuthorityHost(), cap, new Sink(), new SessionTelemetryCollector(),
                    new AiPromptComposer(
                        new BuiltInDefaultAgentSystemPromptProvider(),
                        new NoAgentUserPromptTemplateProvider(),
                        new NullLuaScriptVersionStore(), null, policy, settings),
                    store, policy,
                    new NoOpRoleStructuredResponsePolicy(),
                    new NullAiOrchestrationMetrics(),
                    settings,
                    null, null, null);

                Debug.Log("[ToolDiscovery] ── Request: 'Enchant my iron sword with fire' ──");

                CoreAi.ClearToolCallHistory();
                Task t = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = roleId,
                    Hint = "Enchant my iron sword with fire"
                });
                yield return PlayModeTestAwait.WaitTask(t, 120f, "enchant request");

                // Results
                Debug.Log("[ToolDiscovery] ═══════════════════════════════════════════");
                Debug.Log($"[ToolDiscovery] enchant_weapon called: {_enchantWeaponCalled}");
                Debug.Log($"[ToolDiscovery] Weapon:               '{_enchantTarget}'");
                Debug.Log($"[ToolDiscovery] Enchantment:          '{_enchantType}'");
                Debug.Log($"[ToolDiscovery] Model response:       {cap.LastContent}");
                Debug.Log("[ToolDiscovery] ═══════════════════════════════════════════");

                // Verify model only saw 2 tools (not enchant_weapon directly)
                Assert.That(cap.FirstSystemPrompt, Does.Not.Contain("enchant_weapon"),
                    "enchant_weapon should NOT be in system prompt — only discoverable via read_skill.");

                if (!cap.LastOk)
                {
                    Assert.Inconclusive("LLM did not return a valid response.");
                }

                IReadOnlyList<LlmToolCallRecord> toolCalls = CoreAi.GetToolCallHistorySnapshot();
                Assert.IsTrue(toolCalls.Any(r => r.Status == "completed" &&
                                                 r.Info.RoleId == roleId &&
                                                 r.Info.ToolName == "read_skill"),
                    "Skill discovery must complete a real read_skill tool call.");
                Assert.IsTrue(toolCalls.Any(r => r.Status == "completed" &&
                                                 r.Info.RoleId == roleId &&
                                                 r.Info.ToolName == "call_skill_tool"),
                    "Skill discovery must complete a real call_skill_tool proxy call.");
                Assert.IsTrue(_enchantWeaponCalled,
                    "enchant_weapon should have been executed through call_skill_tool, not only mentioned in model text.");

                Debug.Log("[ToolDiscovery] ✅ Model discovered enchant_weapon via read_skill");
                Debug.Log("[ToolDiscovery] ✅ and executed it via call_skill_tool proxy!");

                ScriptableObject.DestroyImmediate(settings);
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
#endif
}