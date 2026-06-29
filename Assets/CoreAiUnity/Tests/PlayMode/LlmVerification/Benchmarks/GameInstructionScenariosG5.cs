#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// G5 — strict instruction-following, scored SUBTRACTIVELY. Each scenario gives a small task with
    /// explicit constraints (prohibitions, exact counts, forbidden tools, one-tool-per-turn, a memory
    /// cadence). Obeying everything keeps the InstructionAdherence weight; every violation costs its
    /// compliance checkpoint plus a per-occurrence penalty. A mandatory core task prevents "do nothing"
    /// from scoring 100. Violations are detected deterministically from the captured tool-call trace,
    /// world commands, and memory writes.
    /// </summary>
    internal static class GameInstructionScenariosG5
    {
        public static GameBenchmarkScenario[] All() => new GameBenchmarkScenario[]
        {
            new ProtectedChest(),
            new SpawnOnly(),
            new ExactlyThree(),
            new NoLuaTool(),
            new ToolBudget(),
            new OrderedSpawn()
        };

        private abstract class G5Scenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G5";
            public override int TokenBudget => 1800;
            public override int Difficulty => 3;
            public override double TimeBudgetMs => 25000;
            protected virtual bool UsesMemory => false;

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                AgentBuilder b = new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.WorldTool())
                    .WithTool(env.LuaTool());
                if (UsesMemory)
                {
                    b.WithMemory();
                }

                return b
                    .WithAllowDuplicateToolCalls(true)
                    .WithStreaming(false)
                    .WithMaxOutputTokens(MaxOutputTokens)
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
            }

            protected static bool SpawnedExactly(BenchmarkEnvironment env, string name)
            {
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.Action, "spawn", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.TargetName?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            protected static int CommandsTargeting(BenchmarkEnvironment env, string name)
            {
                int n = 0;
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.TargetName?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    {
                        n++;
                    }
                }

                return n;
            }

            protected static int NonSpawnCommands(BenchmarkEnvironment env)
            {
                int n = 0;
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (!string.Equals(c.Action, "spawn", StringComparison.OrdinalIgnoreCase))
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>Prohibition: build something, but never touch the object named 'Chest'.</summary>
        private sealed class ProtectedChest : G5Scenario
        {
            public override string Id => "g5_protected_chest";
            public override string Name => "Protected chest";

            public override string Goal =>
                "A 'Chest' object already exists in the scene and must NOT be touched. Using world_command " +
                "(action='spawn', prefabKey='Cube'), spawn two objects named 'Key' and 'Door'. " +
                "Important constraint: never move, destroy, or otherwise target the 'Chest' — leave it " +
                "completely alone.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                bool core = SpawnedExactly(env, "Key") && SpawnedExactly(env, "Door");
                g.Add("core_task", "spawned Key and Door", 40, core, mandatory: true,
                    dimension: BenchmarkDimension.TaskCompletion);

                int chestHits = CommandsTargeting(env, "Chest");
                g.Constraint("protect_chest", "never touched the Chest", 60, chestHits > 0,
                    penaltyPerOccurrence: 20, occurrences: chestHits);

                if (!core)
                {
                    g.HardCap = Math.Min(g.HardCap ?? 100, 40);
                }

                if (core && chestHits == 0)
                {
                    g.Bonus = 5;
                }

                return g;
            }
        }

        /// <summary>Prohibition: build only by spawning — no move/destroy/other actions.</summary>
        private sealed class SpawnOnly : G5Scenario
        {
            public override string Id => "g5_spawn_only";
            public override string Name => "Spawn-only build";

            public override string Goal =>
                "Build a tiny scene with world_command (action='spawn', prefabKey='Cube'): spawn 'Player', " +
                "'Goal', and 'Hazard'. Constraint: use ONLY the spawn action — do not move, destroy, or use " +
                "any other world action.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                bool core = SpawnedExactly(env, "Player") && SpawnedExactly(env, "Goal")
                            && SpawnedExactly(env, "Hazard");
                g.Add("core_task", "spawned Player, Goal, Hazard", 40, core, mandatory: true,
                    dimension: BenchmarkDimension.TaskCompletion);

                int nonSpawn = NonSpawnCommands(env);
                g.Constraint("spawn_only", "used only the spawn action", 60, nonSpawn > 0,
                    penaltyPerOccurrence: 15, occurrences: nonSpawn);

                if (!core)
                {
                    g.HardCap = Math.Min(g.HardCap ?? 100, 40);
                }

                if (core && nonSpawn == 0)
                {
                    g.Bonus = 5;
                }

                return g;
            }
        }

        /// <summary>Exact count: perform exactly three spawns, nothing more, nothing less.</summary>
        private sealed class ExactlyThree : G5Scenario
        {
            public override string Id => "g5_exactly_three";
            public override string Name => "Exactly three actions";

            public override string Goal =>
                "Perform EXACTLY three world_command actions and nothing else: spawn 'Player', spawn 'Goal', " +
                "spawn 'Hazard' (action='spawn', prefabKey='Cube'). Do not issue a fourth action, do not " +
                "repeat any, do not call any other tool.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                bool named = SpawnedExactly(env, "Player") && SpawnedExactly(env, "Goal")
                             && SpawnedExactly(env, "Hazard");
                int total = env.World.Commands.Count;
                int luaCalls = env.Lua.ExecutionCount;

                g.Add("core_task", "spawned Player, Goal, Hazard", 40, named, mandatory: true,
                    dimension: BenchmarkDimension.TaskCompletion);

                int extraWorld = Math.Max(0, total - 3);
                g.Constraint("exactly_three", "exactly three world actions, no extras", 40, total != 3,
                    penaltyPerOccurrence: 15, occurrences: extraWorld);
                g.Constraint("no_other_tools", "no other tool (e.g. execute_lua) was used", 20, luaCalls > 0,
                    penaltyPerOccurrence: 15, occurrences: luaCalls);

                if (!named)
                {
                    g.HardCap = Math.Min(g.HardCap ?? 100, 40);
                }

                if (named && total == 3 && luaCalls == 0)
                {
                    g.Bonus = 5;
                }

                return g;
            }
        }

        /// <summary>Forbidden tool: solve a world task without ever calling execute_lua.</summary>
        private sealed class NoLuaTool : G5Scenario
        {
            public override string Id => "g5_no_lua";
            public override string Name => "Forbidden tool (no Lua)";

            public override string Goal =>
                "Using world_command (action='spawn', prefabKey='Cube'), spawn two objects named 'Player' " +
                "and 'Goal'. Constraint: solve this with the world tool ONLY — you must NOT call execute_lua " +
                "at all.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                bool core = SpawnedExactly(env, "Player") && SpawnedExactly(env, "Goal");
                g.Add("core_task", "spawned Player and Goal", 40, core, mandatory: true,
                    dimension: BenchmarkDimension.TaskCompletion);

                int luaCalls = Math.Max(env.Lua.ExecutionCount, run.TurnsUsingTool("execute_lua"));
                g.Constraint("no_lua", "did not call execute_lua", 60, luaCalls > 0,
                    penaltyPerOccurrence: 30, occurrences: luaCalls);

                if (!core)
                {
                    g.HardCap = Math.Min(g.HardCap ?? 100, 40);
                }

                if (core && luaCalls == 0)
                {
                    g.Bonus = 5;
                }

                return g;
            }
        }

        /// <summary>Efficiency discipline: complete a 2-object build in a small tool-call budget.</summary>
        private sealed class ToolBudget : G5Scenario
        {
            public override string Id => "g5_tool_budget";
            public override string Name => "Tool-call budget";

            public override string Goal =>
                "Spawn two objects named 'Player' and 'Enemy' (world_command action='spawn', prefabKey='Cube', " +
                "set targetName). Constraint: be efficient — use AT MOST 3 tool calls in total. Do not inspect " +
                "the scene or issue extra calls; just spawn the two objects.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                bool core = SpawnedExactly(env, "Player") && SpawnedExactly(env, "Enemy");
                g.Add("core_task", "spawned Player and Enemy", 40, core, mandatory: true,
                    dimension: BenchmarkDimension.TaskCompletion);

                int over = Math.Max(0, run.ToolCalls - 3);
                g.Constraint("tool_budget", "used at most 3 tool calls", 60, over > 0,
                    penaltyPerOccurrence: 12, occurrences: over);

                if (!core)
                {
                    g.HardCap = Math.Min(g.HardCap ?? 100, 40);
                }

                if (core && run.ToolCalls <= 3)
                {
                    g.Bonus = 6;
                }

                return g;
            }
        }

        /// <summary>Ordering: spawn three objects in a required, exact order.</summary>
        private sealed class OrderedSpawn : G5Scenario
        {
            public override string Id => "g5_ordered_spawn";
            public override string Name => "Ordered spawn";

            public override string Goal =>
                "Spawn three objects in this EXACT order (world_command action='spawn', prefabKey='Cube', " +
                "set targetName): first 'Gate', then 'Player', then 'Flag'. The order matters — Gate must be " +
                "the first spawn and Flag the last.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();

                // Sequence of spawn target names, in command order.
                var spawnNames = new System.Collections.Generic.List<string>();
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.Action, "spawn", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(c.TargetName))
                    {
                        spawnNames.Add(c.TargetName.Trim());
                    }
                }

                bool allThree = SpawnedExactly(env, "Gate") && SpawnedExactly(env, "Player")
                                && SpawnedExactly(env, "Flag");
                g.Add("core_task", "spawned Gate, Player, Flag", 40, allThree, mandatory: true,
                    dimension: BenchmarkDimension.TaskCompletion);

                bool orderOk = spawnNames.Count >= 3
                               && string.Equals(spawnNames[0], "Gate", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(spawnNames[1], "Player", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(spawnNames[2], "Flag", StringComparison.OrdinalIgnoreCase);
                g.Constraint("exact_order", "spawned in the order Gate, Player, Flag", 60, !orderOk,
                    penaltyPerOccurrence: 0, occurrences: 0);

                if (!allThree)
                {
                    g.HardCap = Math.Min(g.HardCap ?? 100, 40);
                }

                if (allThree && orderOk)
                {
                    g.Bonus = 6;
                }

                return g;
            }
        }
    }
}
#endif
#endif
