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
    /// G1 — build a small game from a natural-language spec, driving the real <c>world_command</c> and
    /// <c>execute_lua</c> tools. Graded deterministically by inspecting captured world commands AND
    /// executing the resulting logic slots, using <b>exact unique object names</b> and <b>exact counts</b>
    /// (not substring matches), with over-build penalties. Checkpoints are tagged with the
    /// <see cref="BenchmarkDimension"/> they measure.
    /// </summary>
    internal static class GameBuildScenariosG1
    {
        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new SpawnArena(),
                new CoinCollector(),
                new ConstraintBudget()
            };
        }

        private abstract class WorldBuildScenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G1";

            protected virtual bool UsesLua => true;

            // No scene shots for G1: a handful of scattered primitives photographs as noise and
            // adds nothing next to the G6 hero / G7 puzzle shots (user-reported). Scoring is
            // unaffected — the visual checkpoints read the world state, not the screenshot.
            public override bool CaptureScene => false;
            public override int Difficulty => 2;

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                AgentBuilder b = new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.WorldTool());
                if (UsesLua)
                {
                    b.WithTool(env.LuaTool());
                }

                return b
                    .WithMaxOutputTokens(MaxOutputTokens)
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
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

            protected static int DistinctSpawnCount(BenchmarkEnvironment env)
            {
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.Action, "spawn", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(c.TargetName))
                    {
                        names.Add(c.TargetName.Trim());
                    }
                }

                return names.Count;
            }

            protected static int DistinctSpawnPositionCells(BenchmarkEnvironment env)
            {
                HashSet<string> cells = new(StringComparer.Ordinal);
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.Action, "spawn", StringComparison.OrdinalIgnoreCase))
                    {
                        int x = (int)Math.Round(c.X);
                        int z = (int)Math.Round(c.Z);
                        cells.Add($"{x}:{z}");
                    }
                }

                return cells.Count;
            }

            /// <summary>Tool-correctness: used a tool, with no failed calls or invalid world commands.</summary>
            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run)
            {
                g.Add("used_tools", "issued at least one tool call", 5, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.Add("clean_tools", "no failed tool calls or invalid world commands", 10,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0,
                    dimension: BenchmarkDimension.ToolCorrectness,
                    detail: $"{run.FailedToolCalls} failed, {env.World.InvalidCommandCount} invalid");
            }
        }

        private sealed class SpawnArena : WorldBuildScenario
        {
            public override string Id => "g1_spawn_arena";
            public override string Name => "Spawn arena";

            public override string WhatItChecks =>
                "Spawns a Player + 4 uniquely-named enemies — checks correct named spawns and exact count.";

            public override IReadOnlyList<string> ExpectedSceneObjectNames =>
                new[] { "Player", "Enemy1", "Enemy2", "Enemy3", "Enemy4" };

            protected override bool UsesLua => false;

            public override string Goal =>
                "Set up a small combat arena with the world_command tool: one player and four enemies. Give " +
                "them the clear names 'Player', 'Enemy1', 'Enemy2', 'Enemy3', 'Enemy4', pick whatever " +
                "primitive shape you think best represents each, and place them at distinct positions so they " +
                "don't overlap. Build only what was asked — the player and the four enemies, nothing else.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                bool player = SpawnedExactly(env, "Player");
                int enemies = 0;
                for (int i = 1; i <= 4; i++)
                {
                    if (SpawnedExactly(env, $"Enemy{i}"))
                    {
                        enemies++;
                    }
                }

                int distinct = DistinctSpawnCount(env);
                int totalSpawns = env.World.Count("spawn");
                int occupiedCells = DistinctSpawnPositionCells(env);

                g.Add("player", "spawned 'Player'", 20, player, true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("four_enemies", "spawned Enemy1..Enemy4 (unique names)", 45, enemies == 4,
                    dimension: BenchmarkDimension.TaskCompletion, detail: $"{enemies}/4 uniquely-named enemies");
                g.Add("exact_five", "exactly five distinct objects, nothing extra", 20,
                    distinct == 5 && totalSpawns == 5, dimension: BenchmarkDimension.IntentSequence,
                    detail: $"{distinct} distinct / {totalSpawns} spawn commands");
                g.Add("spatial_spread", "spread the arena objects across distinct x/z positions", 10,
                    occupiedCells >= 3, dimension: BenchmarkDimension.TaskCompletion,
                    detail: $"{occupiedCells} occupied position cells");

                if (totalSpawns > 5)
                {
                    g.Penalty("spawned more than the five requested objects", 5 * (totalSpawns - 5));
                }

                if (player && enemies == 4 && totalSpawns == 5)
                {
                    g.Bonus = 5;
                }

                if (totalSpawns == 0 && run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                return g;
            }
        }

        private sealed class CoinCollector : WorldBuildScenario
        {
            public override string Id => "g1_coin_collector";
            public override string Name => "Coin collector";

            public override string WhatItChecks =>
                "Builds a coin-collector (Player/Coins/Goal) and wires score+win Lua — checks world build + rule logic.";

            public override IReadOnlyList<string> ExpectedSceneObjectNames =>
                new[] { "Player", "Coin1", "Coin2", "Coin3", "Goal" };

            public override int Difficulty => 3;
            public override int TokenBudget => 2200;
            public override double TimeBudgetMs => 35000;
            public override float TimeoutSeconds => 300f;

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("score_formula");
                env.Lua.DeclareSlot("win_condition");
            }

            public override string Goal =>
                "Build a simple coin-collector game.\n" +
                "1. With world_command action='spawn', create the playable objects: a 'Player', three coins " +
                "named 'Coin1', 'Coin2', 'Coin3', and a 'Goal'. Pick fitting primitive shapes yourself and " +
                "place them at distinct positions so the layout is playable. Spawn only these five objects.\n" +
                "2. With execute_lua, define two logic slots: a 'score_formula' that takes the number of coins " +
                "collected and returns the score (one point per coin), and a 'win_condition' that takes the " +
                "score and returns true once the player has collected at least 3 coins. Use " +
                "logic_define('name', function(...) ... end); work out the bodies yourself.\n" +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                bool player = SpawnedExactly(env, "Player");
                int coins = 0;
                for (int i = 1; i <= 3; i++)
                {
                    if (SpawnedExactly(env, $"Coin{i}"))
                    {
                        coins++;
                    }
                }

                bool goal = SpawnedExactly(env, "Goal");
                int totalSpawns = env.World.Count("spawn");
                int occupiedCells = DistinctSpawnPositionCells(env);

                bool scoreOk = Num(env, "score_formula", 0, 0.0) && Num(env, "score_formula", 1, 1.0)
                                                                 && Num(env, "score_formula", 2, 2.0) &&
                                                                 Num(env, "score_formula", 3, 3.0)
                                                                 && Num(env, "score_formula", 5, 5.0) &&
                                                                 Num(env, "score_formula", 9, 9.0);
                bool winBelow = Bool(env, "win_condition", false, 0.0) && Bool(env, "win_condition", false, 1.0)
                                                                       && Bool(env, "win_condition", false, 2.0);
                bool winAt = Bool(env, "win_condition", true, 3.0) && Bool(env, "win_condition", true, 4.0)
                                                                   && Bool(env, "win_condition", true, 9.0);

                g.Add("player", "Player spawned", 8, player, dimension: BenchmarkDimension.TaskCompletion);
                g.Add("three_coins", "Coin1..Coin3 spawned (unique)", 15, coins == 3,
                    dimension: BenchmarkDimension.TaskCompletion, detail: $"{coins}/3 coins");
                g.Add("goal", "Goal spawned", 6, goal, dimension: BenchmarkDimension.TaskCompletion);
                g.Add("no_junk", "exactly five objects, nothing extra", 8, totalSpawns == 5,
                    dimension: BenchmarkDimension.IntentSequence, detail: $"{totalSpawns} spawn commands");
                g.Add("spatial_spread", "spread Player, coins, and Goal across distinct x/z positions", 8,
                    occupiedCells >= 4, dimension: BenchmarkDimension.TaskCompletion,
                    detail: $"{occupiedCells} occupied position cells");
                g.Add("score_formula", "score_formula(n)==n on hidden samples", 22, scoreOk, true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("win_below_false", "win_condition false for 0,1,2", 10, winBelow,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("win_at_true", "win_condition true for 3,4,9", 16, winAt, true,
                    dimension: BenchmarkDimension.TaskCompletion);

                if (totalSpawns > 5)
                {
                    g.Penalty("spawned more than the five requested objects", 5 * (totalSpawns - 5));
                }

                if (player && coins == 3 && goal && totalSpawns == 5 && scoreOk && winAt && winBelow)
                {
                    g.Bonus = 6;
                }

                return g;
            }

            private static bool Num(BenchmarkEnvironment env, string slot, double expected, params object[] args)
            {
                return env.Lua.TryNumber(slot, out double v, args) && Math.Abs(v - expected) < 1e-6;
            }

            private static bool Bool(BenchmarkEnvironment env, string slot, bool expected, params object[] args)
            {
                return env.Lua.TryBool(slot, out bool v, args) && v == expected;
            }
        }

        private sealed class ConstraintBudget : WorldBuildScenario
        {
            public override string Id => "g1_constraint_budget";
            public override string Name => "Constraint budget";

            public override string WhatItChecks =>
                "Spawns exactly Tree/Rock/Bush — checks instruction discipline (no extra or other actions).";

            public override IReadOnlyList<string> ExpectedSceneObjectNames =>
                new[] { "Tree", "Rock", "Bush" };

            protected override bool UsesLua => false;
            public override int TokenBudget => 1200;

            public override string Goal =>
                "Spawn exactly three objects and do nothing else. Use world_command action='spawn' with distinct " +
                "targetName and a fitting primitive prefabKey: 'Tree' (capsule), 'Rock' (sphere), 'Bush' (sphere). " +
                "Place them at three distinct x/z positions. Do not spawn extra objects, do not move or destroy anything.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                bool all = SpawnedExactly(env, "Tree") && SpawnedExactly(env, "Rock") && SpawnedExactly(env, "Bush");
                int totalSpawns = env.World.Count("spawn");
                int otherWorld = env.World.Commands.Count - totalSpawns;
                int occupiedCells = DistinctSpawnPositionCells(env);

                g.Add("three_named", "spawned Tree, Rock, Bush", 45, all, true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("exactly_three", "exactly three spawns, no more", 25, totalSpawns == 3,
                    dimension: BenchmarkDimension.IntentSequence, detail: $"{totalSpawns} spawns");
                g.Add("no_other_actions", "no move/destroy/other world actions", 15, otherWorld == 0,
                    dimension: BenchmarkDimension.IntentSequence, detail: $"{otherWorld} other world command(s)");
                g.Add("spatial_spread", "placed Tree, Rock, and Bush in distinct x/z positions", 10,
                    occupiedCells >= 3, dimension: BenchmarkDimension.TaskCompletion,
                    detail: $"{occupiedCells} occupied position cells");

                if (totalSpawns > 3)
                {
                    g.Penalty("spawned more than three objects", 6 * (totalSpawns - 3));
                }

                if (otherWorld > 0)
                {
                    g.Penalty("used disallowed world actions", 6 * otherWorld);
                }

                if (all && totalSpawns == 3 && otherWorld == 0)
                {
                    g.Bonus = 5;
                }

                return g;
            }
        }
    }
}
#endif
#endif