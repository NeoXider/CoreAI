using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Benchmark: compares a real LLM agent WITH skills (2 meta-tools) vs WITHOUT (19 direct tools).
    /// Measures: system prompt size (chars), tool count sent to LLM, response time.
    /// The agent is a "Game Master" with 4 skills: Crafting, Combat, Lore, Trading.
    /// Each skill has its own tools and long instructions.
    /// </summary>
#if COREAI_LLM && !UNITY_WEBGL
    public sealed class SkillSetBenchmarkPlayModeTests
    {
        private const float BenchmarkTurnTimeoutSeconds = 240f;

        // ── Capturing LLM client ──────────────────────────────────────────────

        private sealed class BenchmarkCaptureLlm : ILlmClient
        {
            private readonly ILlmClient _inner;

            public string LastSystemPrompt;
            public int LastSystemPromptChars;
            public IReadOnlyList<ILlmTool> LastTools;
            public int LastToolCount;
            public string LastContent;
            public bool LastOk;
            public long ElapsedMs;

            public BenchmarkCaptureLlm(ILlmClient inner)
            {
                _inner = inner;
            }

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken ct = default)
            {
                LastSystemPrompt = request.SystemPrompt;
                LastSystemPromptChars = request.SystemPrompt?.Length ?? 0;
                LastTools = request.Tools;
                LastToolCount = request.Tools?.Count ?? 0;

                Stopwatch sw = Stopwatch.StartNew();
                LlmCompletionResult result = await _inner.CompleteAsync(request, ct);
                sw.Stop();
                ElapsedMs = sw.ElapsedMilliseconds;

                LastOk = result is { Ok: true };
                LastContent = result?.Content;
                return result;
            }
        }

        // ── Dummy tools (realistic names and schemas) ─────────────────────────

        private static DelegateLlmTool Tool(string name, string desc)
        {
            return new DelegateLlmTool(name, desc, new Action(() => { }));
        }

        // ── Skills ────────────────────────────────────────────────────────────

        private static SkillSet MakeCraftingSkill()
        {
            return new SkillSet("Crafting",
                "Forge weapons, armor, and items from raw materials",
                @"## Crafting System
You are a master craftsman. When the player asks to craft, follow this protocol:
1. Call get_recipes to check available recipes for the requested item type.
2. Call check_inventory to verify the player has required materials.
3. If materials are sufficient, call craft_item with the recipe_id and quality modifier.
4. Report the result to the player with flavor text about the crafting process.
5. If materials are insufficient, suggest where to find them using get_material_sources.

Quality modifiers: 1.0 = normal, 1.5 = fine, 2.0 = masterwork (requires special tools).
Always check durability of crafting tools before starting.",
                Tool("get_recipes", "Get available crafting recipes. Params: item_type (string)"),
                Tool("check_inventory", "Check player inventory for materials. Params: material_list (string[])"),
                Tool("craft_item", "Craft an item. Params: recipe_id (string), quality_modifier (float)"),
                Tool("get_material_sources", "Find where to gather materials. Params: material_name (string)"));
        }

        private static SkillSet MakeCombatSkill()
        {
            return new SkillSet("Combat",
                "Fight enemies and manage combat encounters",
                @"## Combat System
You manage combat encounters. Follow these rules:
1. Call get_enemy_stats before suggesting tactics.
2. Use calculate_damage to determine attack outcomes.
3. Call apply_status_effect for special attacks (poison, stun, bleed).
4. Track initiative order with get_initiative.
5. Critical hits (nat 20) deal double damage and trigger special effects.
6. Environmental hazards can be triggered with interact_environment.

Damage formula: base_damage * weapon_modifier * (1 + strength/100) - armor_rating.
Always narrate combat dramatically.",
                Tool("get_enemy_stats", "Get enemy statistics. Params: enemy_id (string)"),
                Tool("calculate_damage", "Calculate damage. Params: attacker_id, target_id, weapon_id (string)"),
                Tool("apply_status_effect",
                    "Apply status effect. Params: target_id (string), effect (string), duration (int)"),
                Tool("get_initiative", "Get combat initiative order"),
                Tool("interact_environment", "Interact with environment hazard. Params: hazard_id (string)"));
        }

        private static SkillSet MakeLoreSkill()
        {
            return new SkillSet("Lore",
                "World knowledge, history, and codex entries",
                @"## Lore & Knowledge System
You are the keeper of world knowledge. When asked about lore:
1. Call search_codex to find relevant lore entries.
2. Use get_npc_backstory for character information.
3. Call get_quest_history to check what the player already knows.
4. Use reveal_secret only if the player has completed prerequisite quests.
5. Track discovered lore with mark_lore_discovered.

Never reveal major plot twists without checking prerequisites.
Present lore as in-character narration, not raw data.",
                Tool("search_codex", "Search the lore codex. Params: query (string)"),
                Tool("get_npc_backstory", "Get NPC backstory. Params: npc_id (string)"),
                Tool("get_quest_history", "Get player quest history"),
                Tool("reveal_secret", "Reveal a world secret. Params: secret_id (string)"),
                Tool("mark_lore_discovered", "Mark lore as discovered. Params: lore_id (string)"));
        }

        private static SkillSet MakeTradingSkill()
        {
            return new SkillSet("Trading",
                "Buy, sell, and haggle with merchants",
                @"## Trading System
You handle all commerce. Protocol:
1. Call get_merchant_inventory for available goods and prices.
2. Use check_player_gold to verify funds.
3. Call execute_trade to complete a purchase or sale.
4. Apply haggle_modifier if player attempts negotiation (charisma check).
5. Rare items require reputation check via get_reputation.

Price formula: base_price * supply_demand_modifier * (1 - haggle_discount).
Some merchants only trade specific item types.",
                Tool("get_merchant_inventory", "Get merchant inventory. Params: merchant_id (string)"),
                Tool("check_player_gold", "Check player gold balance"),
                Tool("execute_trade", "Execute trade. Params: item_id (string), quantity (int), is_buying (bool)"),
                Tool("haggle_modifier", "Calculate haggle discount. Params: charisma (int)"),
                Tool("get_reputation", "Get player reputation with faction. Params: faction_id (string)"));
        }

        // ── Infra stubs ───────────────────────────────────────────────────────

        private sealed class Sink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand c)
            {
            }
        }

        private sealed class Tele : ISessionTelemetryProvider
        {
            public GameSessionSnapshot BuildSnapshot()
            {
                return new GameSessionSnapshot();
            }
        }

        private sealed class Sys : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                prompt = "";
                return true;
            }
        }

        private sealed class Usr : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string t)
            {
                t = "{hint}";
                return true;
            }
        }

        // ── Benchmark struct ──────────────────────────────────────────────────

        private struct BenchmarkResult
        {
            public string Label;
            public int SystemPromptChars;
            public int ToolCount;
            public long ElapsedMs;
            public bool Ok;
            public bool TimedOut;
            public int ResponseChars;
        }

        private static void LogResult(BenchmarkResult r)
        {
            Debug.Log($"[SkillBenchmark] ┌── {r.Label} ──");
            Debug.Log(
                $"[SkillBenchmark] │ System prompt: {r.SystemPromptChars} chars (~{r.SystemPromptChars / 4} tokens)");
            Debug.Log($"[SkillBenchmark] │ Tools sent:    {r.ToolCount}");
            Debug.Log($"[SkillBenchmark] │ LLM time:      {r.ElapsedMs} ms");
            Debug.Log($"[SkillBenchmark] │ Timed out:     {r.TimedOut}");
            Debug.Log($"[SkillBenchmark] │ Response:      {r.ResponseChars} chars");
            Debug.Log($"[SkillBenchmark] │ OK:            {r.Ok}");
            Debug.Log($"[SkillBenchmark] └────────────────────────────");
        }

        // ── Test ──────────────────────────────────────────────────────────────

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator Benchmark_WithSkillProxy_VsDirectTools_ComparesTokesAndTime()
        {
            Debug.Log("[SkillBenchmark] ═══════════════════════════════════════════");
            Debug.Log("[SkillBenchmark] BENCHMARK — Skills (2 meta-tools) vs Direct (19 tools)");
            Debug.Log("[SkillBenchmark] ═══════════════════════════════════════════");

            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0.2f, (int)BenchmarkTurnTimeoutSeconds,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);

                SkillSet crafting = MakeCraftingSkill();
                SkillSet combat = MakeCombatSkill();
                SkillSet lore = MakeLoreSkill();
                SkillSet trading = MakeTradingSkill();

                const string basePrompt = "You are the Game Master for an RPG. " +
                                          "Respond briefly. Use tools when appropriate.";
                const string userMsg = "I want to craft an iron sword";

                // ─── Run A: WITH skills (proxy — 2 meta-tools) ─────────────

                Debug.Log("[SkillBenchmark] ── RUN A: WITH Skills (proxy, 2 meta-tools) ──");

                AgentConfig cfgSkills = new AgentBuilder("GM_skills")
                    {
                        SuppressBuildWarnings = true
                    }
                    .WithSystemPrompt(basePrompt)
                    .WithSkill(crafting)
                    .WithSkill(combat)
                    .WithSkill(lore)
                    .WithSkill(trading)
                    .WithMode(AgentMode.ToolsAndChat)
                    .Build();

                BenchmarkResult rA = default;
                {
                    InMemoryStore store = new();
                    AgentMemoryPolicy policy = new();
                    cfgSkills.ApplyToPolicy(policy);
                    BenchmarkCaptureLlm cap = new(handle.WrapWithMemoryStore(store));
                    CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

                    AiOrchestrator orch = new(
                        new SoloAuthorityHost(), cap, new Sink(), new Tele(),
                        new AiPromptComposer(new Sys(), new Usr(), null, null, policy, settings),
                        store, policy,
                        new NoOpRoleStructuredResponsePolicy(),
                        new NullAiOrchestrationMetrics(),
                        settings,
                        new LocalActorIdentityProvider("skill-benchmark-skills"), null, null);

                    using CancellationTokenSource skillsTimeout = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = "GM_skills",
                        Hint = userMsg,
                        MaxOutputTokens = 128000
                    }, skillsTimeout.Token);

                    Stopwatch skillsSw = Stopwatch.StartNew();
                    while (!t.IsCompleted && skillsSw.Elapsed.TotalSeconds < BenchmarkTurnTimeoutSeconds)
                    {
                        yield return null;
                    }

                    skillsSw.Stop();

                    bool skillsTimedOut = !t.IsCompleted;
                    if (skillsTimedOut)
                    {
                        skillsTimeout.Cancel();
                        Debug.LogWarning(
                            $"[SkillBenchmark] with-skills timed out after {BenchmarkTurnTimeoutSeconds:0}s; " +
                            "recording the timeout as benchmark data instead of failing the structural comparison.");
                    }
                    else if (t.IsFaulted)
                    {
                        throw new InvalidOperationException("with-skills benchmark task failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        skillsTimedOut = true;
                    }

                    rA = new BenchmarkResult
                    {
                        Label = "WITH Skills (proxy, 2 meta-tools)",
                        SystemPromptChars = cap.LastSystemPromptChars,
                        ToolCount = cap.LastToolCount,
                        ElapsedMs = cap.ElapsedMs,
                        Ok = cap.LastOk,
                        TimedOut = skillsTimedOut,
                        ResponseChars = cap.LastContent?.Length ?? 0
                    };

                    ScriptableObject.DestroyImmediate(settings);
                }

                LogResult(rA);

                // ─── Run B: WITHOUT skills (all 19 tools registered directly) ──

                Debug.Log("[SkillBenchmark] ── RUN B: WITHOUT Skills (19 direct tools) ──");

                List<ILlmTool> allTools = new();
                foreach (SkillSet skill in new[] { crafting, combat, lore, trading })
                {
                    foreach (ILlmTool tool in skill.Tools)
                    {
                        allTools.Add(tool);
                    }
                }

                AgentBuilder directBuilder = new("GM_direct")
                {
                    SuppressBuildWarnings = true
                };
                directBuilder.WithSystemPrompt(basePrompt);
                foreach (ILlmTool tool in allTools)
                {
                    directBuilder.WithTool(tool);
                }

                directBuilder.WithMode(AgentMode.ToolsAndChat);
                AgentConfig cfgDirect = directBuilder.Build();

                BenchmarkResult rB = default;
                {
                    InMemoryStore store = new();
                    AgentMemoryPolicy policy = new();
                    cfgDirect.ApplyToPolicy(policy);
                    BenchmarkCaptureLlm cap = new(handle.WrapWithMemoryStore(store));
                    CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();

                    AiOrchestrator orch = new(
                        new SoloAuthorityHost(), cap, new Sink(), new Tele(),
                        new AiPromptComposer(new Sys(), new Usr(), null, null, policy, settings),
                        store, policy,
                        new NoOpRoleStructuredResponsePolicy(),
                        new NullAiOrchestrationMetrics(),
                        settings,
                        new LocalActorIdentityProvider("skill-benchmark-direct"), null, null);

                    using CancellationTokenSource directTimeout = new();
                    Task t = orch.RunTaskAsync(new AiTaskRequest
                    {
                        RoleId = "GM_direct",
                        Hint = userMsg,
                        MaxOutputTokens = 128000
                    }, directTimeout.Token);

                    Stopwatch directSw = Stopwatch.StartNew();
                    while (!t.IsCompleted && directSw.Elapsed.TotalSeconds < BenchmarkTurnTimeoutSeconds)
                    {
                        yield return null;
                    }

                    directSw.Stop();

                    bool directTimedOut = !t.IsCompleted;
                    if (directTimedOut)
                    {
                        directTimeout.Cancel();
                        Debug.LogWarning(
                            $"[SkillBenchmark] direct-tools timed out after {BenchmarkTurnTimeoutSeconds:0}s; " +
                            "recording the timeout as benchmark data instead of failing the structural comparison.");
                    }
                    else if (t.IsFaulted)
                    {
                        throw new InvalidOperationException("direct-tools benchmark task failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        throw new OperationCanceledException(
                            "direct-tools benchmark task was canceled before timeout.");
                    }

                    rB = new BenchmarkResult
                    {
                        Label = "WITHOUT Skills (19 direct tools)",
                        SystemPromptChars = cap.LastSystemPromptChars,
                        ToolCount = cap.LastToolCount,
                        ElapsedMs = directTimedOut ? directSw.ElapsedMilliseconds : cap.ElapsedMs,
                        Ok = !directTimedOut && cap.LastOk,
                        TimedOut = directTimedOut,
                        ResponseChars = cap.LastContent?.Length ?? 0
                    };

                    ScriptableObject.DestroyImmediate(settings);
                }

                LogResult(rB);

                // ─── Comparison ────────────────────────────────────────────

                int savedTools = rB.ToolCount - rA.ToolCount;
                int savedChars = rB.SystemPromptChars - rA.SystemPromptChars;
                float promptReduction = rB.SystemPromptChars > 0
                    ? savedChars * 100f / rB.SystemPromptChars
                    : 0;

                Debug.Log("[SkillBenchmark] ═══════════════════════════════════════════");
                Debug.Log("[SkillBenchmark]              COMPARISON");
                Debug.Log("[SkillBenchmark] ═══════════════════════════════════════════");
                Debug.Log($"[SkillBenchmark] Tools:          {rA.ToolCount} (skills) vs {rB.ToolCount} (direct)");
                Debug.Log($"[SkillBenchmark] Tools saved:    {savedTools}");
                Debug.Log($"[SkillBenchmark] Prompt saved:   {savedChars} chars ({promptReduction:0.0}% reduction)");
                Debug.Log($"[SkillBenchmark] Time:           {rA.ElapsedMs}ms vs {rB.ElapsedMs}ms");
                if (rB.ElapsedMs > 0 && rA.ElapsedMs > 0)
                {
                    float speedup = (float)rB.ElapsedMs / rA.ElapsedMs;
                    Debug.Log($"[SkillBenchmark] Speedup:        {speedup:0.0}x");
                }

                Debug.Log("[SkillBenchmark] ═══════════════════════════════════════════");

                // Structural asserts
                Assert.AreEqual(2, rA.ToolCount,
                    "Skills approach should have exactly 2 meta-tools (read_skill + call_skill_tool).");
                Assert.AreEqual(19, rB.ToolCount,
                    "Direct approach should have all 19 tools.");
                Assert.Less(rA.ToolCount, rB.ToolCount,
                    "Skills approach should have fewer tools than direct.");
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
#endif
}
