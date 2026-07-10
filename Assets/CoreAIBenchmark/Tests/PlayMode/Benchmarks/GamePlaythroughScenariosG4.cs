#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// G4 — playable game. The model designs a small RULE SYSTEM as several interacting logic slots; the
    /// harness then <b>simulates a real playthrough</b> by driving state through those slots and asserting
    /// the trajectory step-by-step. This is the showcase tier: it proves the game actually works by its
    /// own rules end-to-end, and it is the hardest discriminator — every slot must be mutually consistent
    /// and handle the edge cases, or the simulation diverges and the run fails.
    /// </summary>
    internal static class GamePlaythroughScenariosG4
    {
        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new CombatPlaythrough(),
                new ShopPlaythrough(),
                new CraftingChainPlaythrough()
            };
        }

        private abstract class G4Scenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G4";
            public override int Difficulty => 5;
            public override int TokenBudget => 3000;
            public override double TimeBudgetMs => 45000;
            public override float TimeoutSeconds => 300f;

            protected const string VerificationNote =
                "After defining the slots, do not call the slot names directly as global Lua functions; " +
                "the benchmark harness will invoke the registered logic slots with hidden samples.";

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.LuaTool())
                    .WithMaxOutputTokens(MaxOutputTokens)
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
            }

            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run)
            {
                g.Add("ran_tool", "issued at least one tool call", 5, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.Add("clean_tool", "no failed Lua executions", 5, env.Lua.FailedExecutions == 0,
                    dimension: BenchmarkDimension.ToolCorrectness);
            }

            protected static bool Num(BenchmarkEnvironment env, string slot, double expected, params object[] a)
            {
                return env.Lua.TryNumber(slot, out double v, a) && Math.Abs(v - expected) < 1e-6;
            }

            protected static bool Bool(BenchmarkEnvironment env, string slot, bool expected, params object[] a)
            {
                return env.Lua.TryBool(slot, out bool v, a) && v == expected;
            }

            protected static bool Str(BenchmarkEnvironment env, string slot, string expected, params object[] a)
            {
                return env.Lua.TryString(slot, out string v, a)
                       && string.Equals((v ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Turn-based combat: damage + clamped HP + death, then simulate a full fight.</summary>
        private sealed class CombatPlaythrough : G4Scenario
        {
            public override string Id => "g4_combat";
            public override string Name => "Combat playthrough";

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("attack_damage");
                env.Lua.DeclareSlot("apply_damage");
                env.Lua.DeclareSlot("is_dead");
            }

            public override string Goal =>
                "Build a turn-based combat system as three logic slots, installed via execute_lua:\n" +
                "1. 'attack_damage' (args: attacker_power, defender_armor) — returns attacker_power minus " +
                "defender_armor, but always at least 1.\n" +
                "2. 'apply_damage' (args: hp, damage) — returns the new hp after taking damage, never below 0.\n" +
                "3. 'is_dead' (args: hp) — returns true when hp is 0 or less.\n" +
                "Use logic_define('name', function(...) ... end) for each. Make the three rules consistent so " +
                "a fight can be simulated with them.\n" +
                VerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                bool installed = env.Lua.LogicSlots.IsOverridden("attack_damage")
                                 && env.Lua.LogicSlots.IsOverridden("apply_damage")
                                 && env.Lua.LogicSlots.IsOverridden("is_dead");
                g.Add("installed", "all three combat slots installed", 10, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);

                g.Add("unit", "attack_damage(12,4)=8, apply_damage(20,8)=12, is_dead(0)=true, is_dead(5)=false",
                    20, Num(env, "attack_damage", 8, 12.0, 4.0) && Num(env, "apply_damage", 12, 20.0, 8.0)
                                                                && Bool(env, "is_dead", true, 0.0) &&
                                                                Bool(env, "is_dead", false, 5.0),
                    dimension: BenchmarkDimension.TaskCompletion);

                g.Add("edges", "attack_damage(3,10)=1 (min 1), apply_damage(5,100)=0 (clamp)", 20,
                    Num(env, "attack_damage", 1, 3.0, 10.0) && Num(env, "apply_damage", 0, 5.0, 100.0),
                    dimension: BenchmarkDimension.Reasoning);

                // Simulated fight: power 12 vs armor 4 (=> 8 dmg/turn) against 20 HP -> 12, 4, 0 over 3 turns.
                List<double> trajectory = new();
                double hp = 20;
                int turns = 0;
                bool simOk = true;
                bool diedAtEnd = false;
                for (int i = 0; i < 12 && hp > 0; i++)
                {
                    if (!env.Lua.TryNumber("attack_damage", out double dmg, 12.0, 4.0)
                        || !env.Lua.TryNumber("apply_damage", out double newHp, hp, dmg))
                    {
                        simOk = false;
                        break;
                    }

                    hp = newHp;
                    turns++;
                    trajectory.Add(hp);
                    env.Lua.TryBool("is_dead", out diedAtEnd, hp);
                    if (diedAtEnd)
                    {
                        break;
                    }
                }

                bool trajectoryOk = simOk && turns == 3 && Math.Abs(hp) < 1e-6 && diedAtEnd
                                    && trajectory.Count == 3
                                    && Math.Abs(trajectory[0] - 12) < 1e-6 && Math.Abs(trajectory[1] - 4) < 1e-6
                                    && Math.Abs(trajectory[2] - 0) < 1e-6;
                g.Add("simulated_fight", "a full fight plays out correctly (HP 20→12→4→0 in 3 turns)", 40,
                    trajectoryOk, true, dimension: BenchmarkDimension.Reasoning,
                    detail: trajectoryOk ? null : $"turns={turns}, finalHp={hp}");

                if (trajectoryOk)
                {
                    g.Bonus = 8;
                }

                return g;
            }
        }

        /// <summary>Shop economy: prices + affordability + purchase, then simulate a buying session.</summary>
        private sealed class ShopPlaythrough : G4Scenario
        {
            public override string Id => "g4_shop";
            public override string Name => "Shop playthrough";

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("item_price");
                env.Lua.DeclareSlot("can_buy");
                env.Lua.DeclareSlot("buy");
            }

            public override string Goal =>
                "Build a shop economy as three logic slots, installed via execute_lua:\n" +
                "1. 'item_price' (arg: item) — returns the price: 'sword'=100, 'potion'=30, 'shield'=75, " +
                "anything else = 0.\n" +
                "2. 'can_buy' (args: gold, price) — returns true when gold is at least price.\n" +
                "3. 'buy' (args: gold, price) — returns the remaining gold after paying price.\n" +
                "Use logic_define for each. Keep them consistent so a shopping session can be simulated.\n" +
                VerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                bool installed = env.Lua.LogicSlots.IsOverridden("item_price")
                                 && env.Lua.LogicSlots.IsOverridden("can_buy")
                                 && env.Lua.LogicSlots.IsOverridden("buy");
                g.Add("installed", "all three shop slots installed", 10, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);

                g.Add("prices", "item_price sword=100, potion=30, shield=75, unknown=0", 20,
                    Num(env, "item_price", 100, "sword") && Num(env, "item_price", 30, "potion")
                                                         && Num(env, "item_price", 75, "shield") &&
                                                         Num(env, "item_price", 0, "rock"),
                    dimension: BenchmarkDimension.TaskCompletion);

                g.Add("rules", "can_buy(150,30)=true, can_buy(20,75)=false, buy(150,30)=120", 20,
                    Bool(env, "can_buy", true, 150.0, 30.0) && Bool(env, "can_buy", false, 20.0, 75.0)
                                                            && Num(env, "buy", 120, 150.0, 30.0),
                    dimension: BenchmarkDimension.TaskCompletion);

                // Simulated session: start 150g. Buy potion(30)->120, sword(100)->20, try shield(75)->rejected.
                bool simOk = true;
                double gold = 150;
                gold = Purchase(env, gold, "potion", ref simOk); // 120
                gold = Purchase(env, gold, "sword", ref simOk); // 20
                double beforeShield = gold;
                gold = Purchase(env, gold, "shield", ref simOk); // rejected -> 20
                bool sessionOk = simOk && Math.Abs(gold - 20) < 1e-6 && Math.Abs(beforeShield - 20) < 1e-6;
                g.Add("simulated_session", "a full shopping run ends with exactly 20 gold (shield rejected)", 40,
                    sessionOk, true, dimension: BenchmarkDimension.Reasoning,
                    detail: sessionOk ? null : $"finalGold={gold}");

                if (sessionOk)
                {
                    g.Bonus = 8;
                }

                return g;
            }

            private static double Purchase(BenchmarkEnvironment env, double gold, string item, ref bool ok)
            {
                if (!env.Lua.TryNumber("item_price", out double price, item)
                    || !env.Lua.TryBool("can_buy", out bool affordable, gold, price))
                {
                    ok = false;
                    return gold;
                }

                if (!affordable)
                {
                    return gold; // rejected — no purchase
                }

                if (!env.Lua.TryNumber("buy", out double remaining, gold, price))
                {
                    ok = false;
                    return gold;
                }

                return remaining;
            }
        }

        /// <summary>Crafting chain: a recipe resolver, then simulate raw wood → plank → table.</summary>
        private sealed class CraftingChainPlaythrough : G4Scenario
        {
            public override string Id => "g4_crafting_chain";
            public override string Name => "Crafting chain playthrough";

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("craft");
            }

            public override string Goal =>
                "Build a crafting system as a single logic slot named 'craft' (args: a, b). Recipes " +
                "(order-independent): wood+wood=plank, plank+plank=table, wood+stone=axe. Any other pair " +
                "returns the string 'none'. Install it via execute_lua as " +
                "logic_define('craft', function(a, b) ... end) — make it consistent so a multi-step " +
                "crafting chain can be simulated (e.g. wood into planks into a table).\n" +
                VerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "craft slot installed", 10, env.Lua.LogicSlots.IsOverridden("craft"),
                    true, dimension: BenchmarkDimension.IntentSequence);

                g.Add("recipes", "wood+wood=plank, plank+plank=table, wood+stone=axe", 20,
                    Str(env, "craft", "plank", "wood", "wood") && Str(env, "craft", "table", "plank", "plank")
                                                               && Str(env, "craft", "axe", "wood", "stone"),
                    dimension: BenchmarkDimension.TaskCompletion);

                g.Add("reject_and_symmetry", "unknown pairs = none, order-independent (stone+wood=axe)", 20,
                    Str(env, "craft", "none", "wood", "gold") && Str(env, "craft", "none", "table", "table")
                                                              && Str(env, "craft", "axe", "stone", "wood"),
                    dimension: BenchmarkDimension.Reasoning);

                // Simulated chain: 4 wood -> 2 planks -> 1 table.
                bool ok1 = env.Lua.TryString("craft", out string plankA, "wood", "wood");
                bool ok2 = env.Lua.TryString("craft", out string plankB, "wood", "wood");
                bool chainOk = ok1 && ok2
                                   && string.Equals((plankA ?? "").Trim(), "plank", StringComparison.OrdinalIgnoreCase)
                                   && string.Equals((plankB ?? "").Trim(), "plank", StringComparison.OrdinalIgnoreCase)
                                   && env.Lua.TryString("craft", out string table, plankA, plankB)
                                   && string.Equals((table ?? "").Trim(), "table", StringComparison.OrdinalIgnoreCase);
                g.Add("simulated_chain", "wood→plank→table chain produces a table", 40, chainOk,
                    true, dimension: BenchmarkDimension.Reasoning,
                    detail: chainOk ? null : "chain did not reach 'table'");

                if (chainOk)
                {
                    g.Bonus = 8;
                }

                return g;
            }
        }
    }
}
#endif
#endif