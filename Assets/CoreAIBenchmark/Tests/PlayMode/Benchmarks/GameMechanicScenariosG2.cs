#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using CoreAI.Ai;
using CoreAI.Benchmarking;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// G2 — runtime mechanic authoring. The game declares named <see cref="LuaLogicSlots"/>; the model
    /// must install Lua overrides via <c>execute_lua</c> (<c>logic_define</c>) to satisfy a behavioral
    /// spec. Graded by EXECUTING the installed slot on a battery of inputs — including <b>hidden</b>
    /// samples not shown in the prompt — so a piecewise/constant fake that only matches the examples does
    /// not pass. Checkpoints are tagged with the <see cref="BenchmarkDimension"/> they measure.
    /// </summary>
    internal static class GameMechanicScenariosG2
    {
        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new FlatDamageBuff(),
                new LevelScaledDamage(),
                new ScoreWinCondition(),
                new MultiArgDamage(),
                new CraftingRules()
            };
        }

        private abstract class LuaSlotScenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G2";
            public override int Difficulty => 2;
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

            protected bool NumberIs(BenchmarkEnvironment env, double expected, params object[] args)
            {
                return env.Lua.TryNumber(Slot, out double v, args) && System.Math.Abs(v - expected) < 1e-6;
            }

            protected bool BoolIs(BenchmarkEnvironment env, bool expected, params object[] args)
            {
                return env.Lua.TryBool(Slot, out bool v, args) && v == expected;
            }

            /// <summary>Common Tool-correctness checkpoint: the model ran Lua without failed executions.</summary>
            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run)
            {
                g.Add("ran_lua", "called execute_lua at least once", 5, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.Add("clean_lua", "no failed Lua executions", 5, env.Lua.FailedExecutions == 0,
                    dimension: BenchmarkDimension.ToolCorrectness,
                    detail: env.Lua.FailedExecutions == 0 ? null : $"{env.Lua.FailedExecutions} failed");
            }
        }

        private sealed class FlatDamageBuff : LuaSlotScenario
        {
            public override string Id => "g2_flat_damage";
            public override string Name => "Flat damage buff";
            public override int Difficulty => 1;
            protected override string Slot => "calculate_damage";

            public override string Goal =>
                "The game has a runtime logic slot 'calculate_damage'. Players say the game is too easy. " +
                "Using the execute_lua tool, run exactly:\n" +
                "logic_define('calculate_damage', function() return 50 end)\n" +
                "so that calculate_damage() returns 50. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                bool installed = env.Lua.LogicSlots.IsOverridden(Slot);
                bool returns50 = NumberIs(env, 50);

                AddToolHygiene(g, env, run);
                g.Add("override_installed", "calculate_damage override installed", 30, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);
                g.Add("returns_50", "calculate_damage() returns 50", 60, returns50, true,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: installed ? null : "slot was never overridden");

                if (!installed && run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                if (returns50)
                {
                    g.Bonus = 4;
                }

                return g;
            }
        }

        private sealed class LevelScaledDamage : LuaSlotScenario
        {
            public override string Id => "g2_level_damage";
            public override string Name => "Level-scaled damage";
            protected override string Slot => "calculate_damage";

            public override string Goal =>
                "The game has a logic slot 'calculate_damage'. Using execute_lua, define it so damage equals " +
                "10 times the level passed as the first argument. Run exactly this shape:\n" +
                "logic_define('calculate_damage', function(level) return 10 * level end)\n" +
                "For example level 1 -> 10, level 5 -> 50. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "override installed", 10, env.Lua.LogicSlots.IsOverridden(Slot), true,
                    dimension: BenchmarkDimension.IntentSequence);
                g.Add("level_1", "damage(1) == 10", 10, NumberIs(env, 10, 1.0),
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("level_5", "damage(5) == 50", 10, NumberIs(env, 50, 5.0), true,
                    dimension: BenchmarkDimension.TaskCompletion);

                bool hidden = NumberIs(env, 0, 0.0) && NumberIs(env, 20, 2.0) && NumberIs(env, 30, 3.0)
                              && NumberIs(env, 70, 7.0) && NumberIs(env, 110, 11.0);
                g.Add("hidden_samples", "damage(0,2,3,7,11) all == 10*level", 45, hidden, true,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: hidden ? null : "fails on inputs not shown in the prompt");

                bool monotonic = env.Lua.TryNumber(Slot, out double d1, 1.0)
                                 && env.Lua.TryNumber(Slot, out double d2, 2.0) && d2 > d1;
                g.Add("monotonic", "damage increases with level", 10, monotonic,
                    dimension: BenchmarkDimension.TaskCompletion);

                if (hidden)
                {
                    g.Bonus = 4;
                }

                return g;
            }
        }

        private sealed class ScoreWinCondition : LuaSlotScenario
        {
            public override string Id => "g2_win_condition";
            public override string Name => "Score win condition";
            protected override string Slot => "win_condition";

            public override string Goal =>
                "The game has a boolean logic slot 'win_condition'. Using execute_lua, define it so the player " +
                "wins exactly when their score (first argument) is at least 100. Run exactly this shape:\n" +
                "logic_define('win_condition', function(score) return score >= 100 end)\n" + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "override installed", 10, env.Lua.LogicSlots.IsOverridden(Slot), true,
                    dimension: BenchmarkDimension.IntentSequence);
                g.Add("at_true", "win(100) == true", 15, BoolIs(env, true, 100.0), true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("below_false", "win(99) == false", 15, BoolIs(env, false, 99.0), true,
                    dimension: BenchmarkDimension.TaskCompletion);

                bool hidden = BoolIs(env, false, 0.0) && BoolIs(env, false, 50.0) && BoolIs(env, true, 101.0)
                              && BoolIs(env, true, 1000.0);
                g.Add("hidden_samples", "win(0,50)=false, win(101,1000)=true", 45, hidden, true,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: hidden ? null : "fails on inputs not shown in the prompt");

                if (hidden)
                {
                    g.Bonus = 4;
                }

                return g;
            }
        }

        private sealed class MultiArgDamage : LuaSlotScenario
        {
            public override string Id => "g2_multi_arg_damage";
            public override string Name => "Multi-arg damage formula";
            public override int Difficulty => 3;
            protected override string Slot => "calculate_damage";
            public override int TokenBudget => 1800;
            public override float TimeoutSeconds => 240f;

            public override string Goal =>
                "The game has a logic slot 'calculate_damage' that receives three arguments: base, level, armor. " +
                "Using execute_lua, define it so the result is base + level*10 - armor, but never below 0. " +
                "Run a shape like:\n" +
                "logic_define('calculate_damage', function(base, level, armor) " +
                "local d = base + level*10 - armor; if d < 0 then d = 0 end; return d end)\n" + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "override installed", 10, env.Lua.LogicSlots.IsOverridden(Slot), true,
                    dimension: BenchmarkDimension.IntentSequence);
                g.Add("basic", "damage(10,1,0) == 20", 20, NumberIs(env, 20, 10.0, 1.0, 0.0), true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("with_armor", "damage(10,2,5) == 25", 20, NumberIs(env, 25, 10.0, 2.0, 5.0),
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("hidden", "damage(0,3,1)==29 and damage(5,1,3)==12", 20,
                    NumberIs(env, 29, 0.0, 3.0, 1.0) && NumberIs(env, 12, 5.0, 1.0, 3.0),
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("clamped", "damage(0,0,50) == 0 (never negative)", 10, NumberIs(env, 0, 0.0, 0.0, 50.0),
                    true, dimension: BenchmarkDimension.TaskCompletion);

                if (NumberIs(env, 20, 10.0, 1.0, 0.0) && NumberIs(env, 0, 0.0, 0.0, 50.0))
                {
                    g.Bonus = 5;
                }

                return g;
            }
        }

        /// <summary>
        /// Game-mechanic test (a harder crafting system, inspired by the crafting tests): the model
        /// authors a deterministic recipe resolver in Lua. Graded for correctness on every listed recipe,
        /// rejection of unknown pairs (hidden), AND determinism (same inputs twice → identical output).
        /// </summary>
        private sealed class CraftingRules : LuaSlotScenario
        {
            public override string Id => "g2_crafting_rules";
            public override string Name => "Crafting rules engine";
            public override int Difficulty => 3;
            protected override string Slot => "craft_result";
            public override int TokenBudget => 2400;
            public override double TimeBudgetMs => 35000;
            public override float TimeoutSeconds => 300f;

            public override string Goal =>
                "The game has a logic slot 'craft_result(a, b)' that returns the item crafted from two " +
                "ingredients, or the string 'none' if the pair is not a valid recipe. The recipes are:\n" +
                "  wood  + wood = plank\n" +
                "  plank + plank = table\n" +
                "  iron  + coal = steel\n" +
                "  wood  + coal = torch\n" +
                "Recipes are order-independent (wood+coal and coal+wood both give torch). " +
                "Using execute_lua, define the slot, for example:\n" +
                "logic_define('craft_result', function(a, b) ... return 'plank' ... end)\n" + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "craft_result override installed", 10, env.Lua.LogicSlots.IsOverridden(Slot),
                    true, dimension: BenchmarkDimension.IntentSequence);

                bool recipes = Str(env, "plank", "wood", "wood") && Str(env, "table", "plank", "plank")
                                                                 && Str(env, "steel", "iron", "coal") &&
                                                                 Str(env, "torch", "wood", "coal");
                g.Add("recipes", "all four recipes craft correctly", 35, recipes, true,
                    dimension: BenchmarkDimension.TaskCompletion);

                bool symmetric = Str(env, "torch", "coal", "wood") && Str(env, "steel", "coal", "iron");
                g.Add("order_independent", "ingredient order does not matter", 15, symmetric,
                    dimension: BenchmarkDimension.TaskCompletion);

                bool rejects = Str(env, "none", "wood", "iron") && Str(env, "none", "coal", "coal")
                                                                && Str(env, "none", "stone", "stone");
                g.Add("rejects_unknown", "unknown pairs return 'none' (hidden)", 20, rejects, true,
                    dimension: BenchmarkDimension.TaskCompletion);

                // Determinism: the same inputs must yield the same output across repeated calls.
                bool deterministic = SameTwice(env, "wood", "wood") && SameTwice(env, "iron", "coal")
                                                                    && SameTwice(env, "stone", "stone");
                g.Add("deterministic", "identical inputs give identical outputs", 20, deterministic,
                    true, dimension: BenchmarkDimension.Determinism,
                    detail: deterministic ? null : "output changed between identical calls");

                if (recipes && rejects && deterministic)
                {
                    g.Bonus = 6;
                }

                return g;
            }

            private bool Str(BenchmarkEnvironment env, string expected, string a, string b)
            {
                return env.Lua.TryString(Slot, out string v, a, b)
                       && string.Equals((v ?? "").Trim(), expected, System.StringComparison.OrdinalIgnoreCase);
            }

            private bool SameTwice(BenchmarkEnvironment env, string a, string b)
            {
                bool ok1 = env.Lua.TryString(Slot, out string v1, a, b);
                bool ok2 = env.Lua.TryString(Slot, out string v2, a, b);
                return ok1 && ok2 && string.Equals(v1 ?? "", v2 ?? "", System.StringComparison.Ordinal);
            }
        }
    }
}
#endif
#endif
