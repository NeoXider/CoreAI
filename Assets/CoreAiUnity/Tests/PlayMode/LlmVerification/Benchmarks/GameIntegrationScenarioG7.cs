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
    /// G7 — comprehensive integration. G1-G6 each isolate one skill (spatial build, Lua logic, reasoning,
    /// simulated playthrough, strict instructions, free-form visual); a model can specialize on one and
    /// still clear the bar, which is why even a 4B reference model scores 90+ across the suite. G7 forces
    /// ALL of those skills to work together in a single coherent task AND cross-checks that the model's own
    /// spatial choices (recorded from its <c>world_command</c> spawns) stay CONSISTENT with the logic it
    /// wrote for <c>execute_lua</c> — a model can get spawning right and logic right in isolation while
    /// still being inconsistent between the two (e.g. writing a distance check that assumes a different
    /// key position than the one it actually spawned). That cross-consistency check is the differentiator:
    /// it cannot be gamed by memorized canned I/O the way an isolated unit check can.
    /// Runs once regardless of suite reps (<see cref="GameBenchmarkScenario.RepsOverride"/>) — a heavy,
    /// one-off comprehensive scenario, same convention as the G6 visual hero.
    /// </summary>
    internal static class GameIntegrationScenariosG7
    {
        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new KeyPuzzle()
            };
        }

        private abstract class G7Scenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G7";
            public override int Difficulty => 9;
            public override bool CaptureScene => true;
            public override int? RepsOverride => 1;
            public override int TokenBudget => 2500;
            public override double TimeBudgetMs => 40000;
            public override float TimeoutSeconds => 300f;

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

            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run)
            {
                g.Add("clean_tools", "no failed tool calls or invalid world commands", 5,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0 && env.Lua.FailedExecutions == 0,
                    dimension: BenchmarkDimension.ToolCorrectness);
            }

            protected static List<RecordedWorldCommand> SpawnCommands(BenchmarkEnvironment env)
            {
                List<RecordedWorldCommand> result = new();
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.Action, "spawn", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(c);
                    }
                }

                return result;
            }

            protected static bool TryFindSpawn(List<RecordedWorldCommand> spawns, string exactName,
                out RecordedWorldCommand found)
            {
                foreach (RecordedWorldCommand c in spawns)
                {
                    if (string.Equals(c.TargetName?.Trim(), exactName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = c;
                        return true;
                    }
                }

                found = null;
                return false;
            }
        }

        /// <summary>
        /// Spawn Player/Gate/Key in specific zones and exact order, then a single Lua distance-threshold
        /// slot — graded not just on each piece in isolation, but on whether the slot behaves correctly
        /// when fed the model's OWN recorded Key position.
        /// </summary>
        private sealed class KeyPuzzle : G7Scenario
        {
            public override string Id => "g7_key_puzzle";
            public override string Name => "Integration puzzle";

            public override string WhatItChecks =>
                "Comprehensive: exact spawn order/zones + a Lua logic slot that must stay consistent with " +
                "the model's own spawned Key position — not just correct in isolation.";

            // logic_define throws "slot not declared by the game" for any name the game didn't pre-declare
            // (LuaLogicSlots.Define) — without this, the model's logic_define('key_found', ...) call would
            // always fail, exactly like G4's playthrough scenarios declare their slots up front.
            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("key_found");
            }

            public override string Goal =>
                "This is a comprehensive test combining spatial building and game logic. Follow every step " +
                "exactly.\n\n" +
                "Step 1 — using world_command action='spawn', create EXACTLY these 3 objects, IN THIS ORDER, " +
                "with these exact targetNames:\n" +
                "1. 'Player' — prefabKey='capsule', y=1, x between -6 and 6, z between -8 and -4.\n" +
                "2. 'Gate' — prefabKey='cube', y=1, x between -6 and 6, z between 4 and 8.\n" +
                "3. 'Key' — prefabKey='sphere', y=1, x between -6 and 6, z between -3 and 3.\n" +
                "Do not spawn any other objects.\n\n" +
                "Step 2 — using execute_lua, define EXACTLY one logic slot named 'key_found' via " +
                "logic_define('key_found', function(player_x, player_z, key_x, key_z) ... end). It must " +
                "return true when the straight-line distance between (player_x, player_z) and " +
                "(key_x, key_z) in the x/z plane is 2.0 units or less, and false otherwise — use " +
                "math.sqrt((player_x-key_x)^2 + (player_z-key_z)^2) <= 2.0. Do not define any other logic " +
                "slots. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                List<RecordedWorldCommand> spawns = SpawnCommands(env);
                bool hasPlayer = TryFindSpawn(spawns, "Player", out RecordedWorldCommand player);
                bool hasGate = TryFindSpawn(spawns, "Gate", out RecordedWorldCommand gate);
                bool hasKey = TryFindSpawn(spawns, "Key", out RecordedWorldCommand key);
                bool spawnedAllThree = hasPlayer && hasGate && hasKey;
                g.Add("spawned_all_three", "spawned Player, Gate, and Key", 15, spawnedAllThree, true,
                    dimension: BenchmarkDimension.TaskCompletion);

                // Exactly 3 (not >=3) and in order: a 4th spawn after Key must fail this, matching the
                // "do not spawn any other objects" instruction.
                bool exactOrder = spawns.Count == 3
                                  && string.Equals(spawns[0].TargetName?.Trim(), "Player",
                                      StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(spawns[1].TargetName?.Trim(), "Gate",
                                      StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(spawns[2].TargetName?.Trim(), "Key",
                                      StringComparison.OrdinalIgnoreCase);
                g.Constraint("exact_count_and_order", "spawned exactly 3 objects, in order Player, Gate, Key",
                    15, !exactOrder, 5,
                    Math.Max(0, spawns.Count - 3));

                bool zonesOk = spawnedAllThree
                               && InRange(player.X, -6f, 6f) && InRange(player.Z, -8f, -4f)
                               && InRange(gate.X, -6f, 6f) && InRange(gate.Z, 4f, 8f)
                               && InRange(key.X, -6f, 6f) && InRange(key.Z, -3f, 3f);
                g.Add("zone_compliance", "each object placed within its required x/z zone", 15, zonesOk,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: spawnedAllThree ? null : "missing object(s), zones not checked");

                bool shapesOk = spawnedAllThree
                                && IsPrefab(player, "capsule") && IsPrefab(gate, "cube") && IsPrefab(key, "sphere");
                g.Add("shape_compliance", "used the specified prefabKey for each object (capsule/cube/sphere)",
                    10, shapesOk, dimension: BenchmarkDimension.ToolCorrectness,
                    detail: spawnedAllThree ? null : "missing object(s), shapes not checked");

                bool logicInstalled = env.Lua.LogicSlots.IsOverridden("key_found");
                g.Add("logic_installed", "key_found logic slot installed", 10, logicInstalled, true,
                    dimension: BenchmarkDimension.IntentSequence);

                bool unitOk = logicInstalled
                              && Bool(env, "key_found", true, 0.0, 0.0, 0.0, 0.0)
                              && Bool(env, "key_found", true, 2.0, 0.0, 0.0, 0.0)
                              && Bool(env, "key_found", false, 3.0, 0.0, 0.0, 0.0);
                g.Add("unit_correctness", "key_found(0,0,0,0)=true, (2,0,0,0)=true, (3,0,0,0)=false", 15,
                    unitOk, dimension: BenchmarkDimension.TaskCompletion);

                // The differentiator: feed the model's OWN recorded Key position back into its OWN logic
                // slot. A model that hardcoded/assumed a different key position, or got the distance
                // formula subtly wrong, fails here even if the isolated unit checks above happen to pass.
                bool integrationOk = false;
                string integrationDetail = "Key not spawned";
                if (hasKey && logicInstalled)
                {
                    double kx = key.X, kz = key.Z;
                    bool atKey = Bool(env, "key_found", true, kx, kz, kx, kz);
                    bool nearKey = Bool(env, "key_found", true, kx + 1.5, kz, kx, kz);
                    bool farFromKey = Bool(env, "key_found", false, kx + 2.5, kz, kx, kz);
                    integrationOk = atKey && nearKey && farFromKey;
                    integrationDetail = integrationOk
                        ? null
                        : $"atKey={atKey}, nearKey(+1.5)={nearKey}, farFromKey(+2.5)={farFromKey}, key=({kx:0.##},{kz:0.##})";
                }

                g.Add("integration_consistency",
                    "key_found stays correct when fed the model's own spawned Key position", 30, integrationOk,
                    dimension: BenchmarkDimension.Reasoning, detail: integrationDetail);

                if (integrationOk && exactOrder && zonesOk && shapesOk)
                {
                    g.Bonus = 8;
                }

                return g;
            }

            private static bool InRange(float value, float min, float max)
            {
                return value >= min && value <= max;
            }

            private static bool IsPrefab(RecordedWorldCommand c, string expectedPrefabKey)
            {
                return string.Equals(c.PrefabKeyOrName?.Trim(), expectedPrefabKey, StringComparison.OrdinalIgnoreCase);
            }

            private static bool Bool(BenchmarkEnvironment env, string slot, bool expected, params object[] a)
            {
                return env.Lua.TryBool(slot, out bool v, a) && v == expected;
            }
        }
    }
}
#endif
#endif