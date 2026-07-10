#if !COREAI_NO_LUA
#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// G3 — reasoning &amp; design. Unlike G1/G2, the prompt does NOT spell out the Lua code: the model
    /// must work out the mechanic itself (piecewise pricing, recursion, two-sided clamping, boolean
    /// composition, constraint satisfaction). Graded deterministically by executing the resulting slot on
    /// <b>derived</b> inputs — those checkpoints are tagged <see cref="BenchmarkDimension.Reasoning"/>, the
    /// axis that separates "follows instructions" (any 4B model) from "is actually smart". These tasks are
    /// still real game mechanics, so a high score also means the model is fit to drive the game.
    /// </summary>
    internal static class GameReasoningScenariosG3
    {
        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new QuadraticScore(),
                new TieredPricing(),
                new ClampedRegen(),
                new FibonacciRewards(),
                new BooleanDungeon(),
                new BalancedEnemies()
            };
        }

        // --- bases -----------------------------------------------------------------------------------

        private abstract class G3Scenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G3";
            public override int Difficulty => 4;
            public override int TokenBudget => 2600;
            public override double TimeBudgetMs => 35000;
            public override float TimeoutSeconds => 300f;

            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run)
            {
                g.Add("ran_tool", "issued at least one tool call", 5, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.Add("clean_tool", "no failed tool calls or invalid commands", 5,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0,
                    dimension: BenchmarkDimension.ToolCorrectness);
            }

            protected static bool SpawnedExactly(BenchmarkEnvironment env, string exactName)
            {
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.Action, "spawn", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.TargetName?.Trim(), exactName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private abstract class LuaG3Scenario : G3Scenario
        {
            protected abstract string Slot { get; }

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot(Slot);
            }

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.LuaTool())
                    .WithMaxOutputTokens(MaxOutputTokens)
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
            }

            protected bool Num(BenchmarkEnvironment env, double expected, params object[] args)
            {
                return env.Lua.TryNumber(Slot, out double v, args) && Math.Abs(v - expected) < 1e-6;
            }

            protected bool Bool(BenchmarkEnvironment env, bool expected, params object[] args)
            {
                return env.Lua.TryBool(Slot, out bool v, args) && v == expected;
            }
        }

        // --- scenarios -------------------------------------------------------------------------------

        /// <summary>combo² × 10 — simple non-linear formula the model must derive.</summary>
        private sealed class QuadraticScore : LuaG3Scenario
        {
            public override string Id => "g3_quadratic_score";
            public override string Name => "Quadratic combo score";
            protected override string Slot => "score";

            public override string Goal =>
                "Define a logic slot named 'score' that takes one argument 'combo': the score equals the " +
                "combo squared, multiplied by 10. Install it with the execute_lua tool as " +
                "logic_define('score', function(combo) ... end) — work out the body yourself, do not " +
                "hardcode a table of values. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "score slot installed", 10, env.Lua.LogicSlots.IsOverridden(Slot),
                    true, dimension: BenchmarkDimension.IntentSequence);
                g.Add("stated", "score(1)==10", 10, Num(env, 10, 1.0), dimension: BenchmarkDimension.TaskCompletion);
                g.Add("derived", "score(0,2,3,5,10) == combo²·10", 65,
                    Num(env, 0, 0.0) && Num(env, 40, 2.0) && Num(env, 90, 3.0) && Num(env, 250, 5.0)
                    && Num(env, 1000, 10.0), true, dimension: BenchmarkDimension.Reasoning);

                if (Num(env, 1000, 10.0))
                {
                    g.Bonus = 5;
                }

                return g;
            }
        }

        /// <summary>Piecewise tiered pricing — multi-tier reasoning, no formula given.</summary>
        private sealed class TieredPricing : LuaG3Scenario
        {
            public override string Id => "g3_tiered_pricing";
            public override string Name => "Tiered shop pricing";
            protected override string Slot => "price";

            public override string Goal =>
                "Define a logic slot named 'price' that takes one argument 'qty' for a shop. Pricing tiers: " +
                "the first 10 units cost 5 gold each; units 11 to 20 cost 4 gold each; every unit beyond 20 " +
                "costs 3 gold each. price returns the TOTAL cost of buying qty units. Install it via " +
                "execute_lua as logic_define('price', function(qty) ... end) — derive the piecewise formula " +
                "yourself. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "price slot installed", 10, env.Lua.LogicSlots.IsOverridden(Slot),
                    true, dimension: BenchmarkDimension.IntentSequence);

                // Within tier 1 — relatively easy.
                g.Add("tier1", "price(5)==25 and price(10)==50", 15,
                    Num(env, 25, 5.0) && Num(env, 50, 10.0), dimension: BenchmarkDimension.TaskCompletion);

                // Cross-tier — needs real piecewise reasoning.
                bool reason = Num(env, 54, 11.0) // 50 + 1*4
                              && Num(env, 70, 15.0) // 50 + 5*4
                              && Num(env, 90, 20.0) // 50 + 10*4
                              && Num(env, 105, 25.0) // 90 + 5*3
                              && Num(env, 120, 30.0); // 90 + 10*3
                g.Add("cross_tier", "price(11,15,20,25,30) follow the tiers", 65, reason,
                    true, dimension: BenchmarkDimension.Reasoning);

                if (reason)
                {
                    g.Bonus = 6;
                }

                return g;
            }
        }

        /// <summary>Two-sided clamp on a 3-arg function — derive the bounds logic.</summary>
        private sealed class ClampedRegen : LuaG3Scenario
        {
            public override string Id => "g3_clamped_regen";
            public override string Name => "Clamped HP regen";
            protected override string Slot => "regen";

            public override string Goal =>
                "Define a logic slot named 'regen' that takes three arguments (hp, amount, max_hp) for a " +
                "healing system: it returns hp + amount, but the result must never exceed max_hp and never " +
                "drop below 0. Install it via execute_lua as " +
                "logic_define('regen', function(hp, amount, max_hp) ... end) — figure out the clamping yourself. " +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "regen slot installed", 10, env.Lua.LogicSlots.IsOverridden(Slot),
                    true, dimension: BenchmarkDimension.IntentSequence);
                g.Add("normal", "regen(50,30,100)==80", 15, Num(env, 80, 50.0, 30.0, 100.0),
                    dimension: BenchmarkDimension.TaskCompletion);

                bool clampHigh = Num(env, 100, 90.0, 20.0, 100.0) && Num(env, 100, 100.0, 50.0, 100.0);
                bool clampLow = Num(env, 0, 10.0, -30.0, 100.0) && Num(env, 0, 0.0, -5.0, 100.0);
                g.Add("clamp_high", "never exceeds max_hp", 35, clampHigh, true,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("clamp_low", "never drops below 0", 30, clampLow, true,
                    dimension: BenchmarkDimension.Reasoning);

                if (clampHigh && clampLow)
                {
                    g.Bonus = 6;
                }

                return g;
            }
        }

        /// <summary>n-th Fibonacci — recursion/iteration the model must implement, not transcribe.</summary>
        private sealed class FibonacciRewards : LuaG3Scenario
        {
            public override string Id => "g3_fibonacci_rewards";
            public override string Name => "Fibonacci wave rewards";
            public override int Difficulty => 5;
            protected override string Slot => "wave_reward";

            public override string Goal =>
                "Define a logic slot named 'wave_reward' that takes one argument 'n' and returns the n-th " +
                "Fibonacci number, where wave_reward(1)=1, wave_reward(2)=1, wave_reward(3)=2, " +
                "wave_reward(4)=3, wave_reward(5)=5, and each next value is the sum of the two previous ones. " +
                "It must compute the value for any n — do not hardcode a fixed list. Install it via " +
                "execute_lua as logic_define('wave_reward', function(n) ... end). " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "wave_reward slot installed", 10, env.Lua.LogicSlots.IsOverridden(Slot),
                    true, dimension: BenchmarkDimension.IntentSequence);
                g.Add("stated", "wave_reward(1)=1, (5)=5", 10, Num(env, 1, 1.0) && Num(env, 5, 5.0),
                    dimension: BenchmarkDimension.TaskCompletion);

                bool derived = Num(env, 8, 6.0) && Num(env, 13, 7.0) && Num(env, 55, 10.0) && Num(env, 144, 12.0);
                g.Add("derived", "wave_reward(6,7,10,12) = 8,13,55,144", 60, derived, true,
                    dimension: BenchmarkDimension.Reasoning);

                bool deterministic = env.Lua.TryNumber(Slot, out double a, 10.0)
                                     && env.Lua.TryNumber(Slot, out double b, 10.0) && Math.Abs(a - b) < 1e-6;
                g.Add("deterministic", "same n gives same reward", 10, deterministic,
                    dimension: BenchmarkDimension.Determinism);

                if (derived)
                {
                    g.Bonus = 7;
                }

                return g;
            }
        }

        // --- world + lua reasoning -------------------------------------------------------------------

        private abstract class WorldLuaG3Scenario : G3Scenario
        {
            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.WorldTool())
                    .WithTool(env.LuaTool())
                    .WithMaxOutputTokens(MaxOutputTokens)
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
            }
        }

        /// <summary>Boolean composition with negation across three flags.</summary>
        private sealed class BooleanDungeon : WorldLuaG3Scenario
        {
            public override string Id => "g3_boolean_dungeon";
            public override string Name => "Dungeon win logic";

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("can_win");
            }

            public override string Goal =>
                "Build a tiny dungeon. First, with world_command (action='spawn', prefabKey='Cube'), spawn " +
                "three objects named 'Player', 'Key', 'Door'. Then define a boolean logic slot named " +
                "'can_win' that takes three arguments (has_key, at_door, boss_alive) so the player wins ONLY " +
                "when they have the key AND are at the door AND the boss is NOT alive. Install it via " +
                "execute_lua as logic_define('can_win', function(has_key, at_door, boss_alive) ... end). " +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                bool spawned = SpawnedExactly(env, "Player") && SpawnedExactly(env, "Key")
                                                             && SpawnedExactly(env, "Door");
                g.Add("spawned", "Player, Key, Door spawned", 15, spawned,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("installed", "can_win slot installed", 10, env.Lua.LogicSlots.IsOverridden("can_win"),
                    true, dimension: BenchmarkDimension.IntentSequence);

                g.Add("win_case", "can_win(true,true,false)==true", 20, B(env, true, true, true, false),
                    true, dimension: BenchmarkDimension.TaskCompletion);

                bool logic = B(env, false, true, true, true) // boss alive -> false (negation)
                             && B(env, false, true, false, false) // not at door -> false
                             && B(env, false, false, true, false) // no key -> false
                             && B(env, false, false, false, false);
                g.Add("logic", "all non-winning combinations are false", 50, logic, true,
                    dimension: BenchmarkDimension.Reasoning);

                if (logic && B(env, true, true, true, false))
                {
                    g.Bonus = 6;
                }

                return g;
            }

            private static bool B(BenchmarkEnvironment env, bool expected, bool hasKey, bool atDoor, bool bossAlive)
            {
                return env.Lua.TryBool("can_win", out bool v, hasKey, atDoor, bossAlive) && v == expected;
            }
        }

        /// <summary>Constraint satisfaction: four distinct in-range HP values summing to exactly 400.</summary>
        private sealed class BalancedEnemies : WorldLuaG3Scenario
        {
            public override string Id => "g3_balanced_enemies";
            public override string Name => "Balanced enemy HP";
            public override int Difficulty => 5;

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("enemy_hp");
            }

            public override string Goal =>
                "Spawn four enemies with world_command (action='spawn', prefabKey='Cube') named 'Enemy1', " +
                "'Enemy2', 'Enemy3', 'Enemy4'. Then define a logic slot named 'enemy_hp' that takes one " +
                "argument 'name' and returns that enemy's HP. The four HP values must all be DIFFERENT, each " +
                "between 50 and 200 inclusive, and add up to EXACTLY 400. Choose values that satisfy every " +
                "constraint. Install it via execute_lua as logic_define('enemy_hp', function(name) ... end). " +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                int spawns = 0;
                for (int i = 1; i <= 4; i++)
                {
                    if (SpawnedExactly(env, $"Enemy{i}"))
                    {
                        spawns++;
                    }
                }

                g.Add("spawned", "Enemy1..Enemy4 spawned", 15, spawns == 4,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("installed", "enemy_hp slot installed", 10, env.Lua.LogicSlots.IsOverridden("enemy_hp"),
                    true, dimension: BenchmarkDimension.IntentSequence);

                List<double> hp = new();
                bool allResolved = true;
                for (int i = 1; i <= 4; i++)
                {
                    if (env.Lua.TryNumber("enemy_hp", out double v, $"Enemy{i}"))
                    {
                        hp.Add(v);
                    }
                    else
                    {
                        allResolved = false;
                    }
                }

                bool inRange = allResolved && hp.TrueForAll(v => v >= 50 && v <= 200);
                bool distinct = allResolved && new HashSet<double>(hp).Count == hp.Count;
                double sum = 0;
                foreach (double v in hp)
                {
                    sum += v;
                }

                bool sums = allResolved && Math.Abs(sum - 400) < 1e-6;

                g.Add("resolved", "enemy_hp returns a value for each enemy", 15, allResolved,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("in_range", "every HP within 50..200", 15, inRange,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("distinct", "all four HP values are different", 15, distinct,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("sum_400", "HP values sum to exactly 400", 15, sums, true,
                    dimension: BenchmarkDimension.Reasoning,
                    detail: allResolved ? $"sum={sum}" : "unresolved");

                if (spawns == 4 && inRange && distinct && sums)
                {
                    g.Bonus = 7;
                }

                return g;
            }
        }
    }
}
#endif
#endif