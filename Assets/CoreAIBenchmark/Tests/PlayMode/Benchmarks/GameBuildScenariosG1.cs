#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using UnityEngine;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// G1 — build a small game from a natural-language spec, driving the real <c>world_command</c> and
    /// <c>execute_lua</c> tools. Graded deterministically by inspecting the final world state the captured
    /// commands leave behind AND executing the resulting logic slots, using <b>exact unique object names</b>
    /// and <b>exact counts</b> (not substring matches), with over-build penalties. Checkpoints are tagged with
    /// the <see cref="BenchmarkDimension"/> they measure.
    /// </summary>
    internal static class GameBuildScenariosG1
    {
        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new SpawnArena(),
                new CoinCollector(),
                new ConstraintBudget(),
                new ExtremeFortress()
            };
        }

        private abstract class WorldBuildScenario : GameBenchmarkScenario
        {
            /// <summary>Smallest x/z centre distance at which two unscaled 1 m primitives no longer overlap.</summary>
            protected const double MinSeparation = 1.0;

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

            /// <summary>
            /// True when every named object exists in the final scene and each pair of them stands at least
            /// <see cref="MinSeparation"/> apart in x/z. WHY: the old integer-cell count passed two objects
            /// stacked on the same spot as long as enough of the others were spread out.
            /// </summary>
            protected static bool PairwiseSeparated(RecordedScene scene, IReadOnlyList<string> names,
                out string detail)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    if (!scene.TryGet(names[i], out RecordedSceneObject a))
                    {
                        detail = $"'{names[i]}' missing";
                        return false;
                    }

                    for (int j = i + 1; j < names.Count; j++)
                    {
                        if (!scene.TryGet(names[j], out RecordedSceneObject b))
                        {
                            continue;
                        }

                        double dx = a.X - b.X;
                        double dz = a.Z - b.Z;
                        double distance = Math.Sqrt(dx * dx + dz * dz);
                        if (distance < MinSeparation)
                        {
                            detail = string.Format(CultureInfo.InvariantCulture,
                                "'{0}' and '{1}' only {2:0.##} m apart", a.Name, b.Name, distance);
                            return false;
                        }
                    }
                }

                detail = null;
                return true;
            }

            /// <summary>Tool-correctness: used a tool, with no failed calls or invalid world commands.</summary>
            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run)
            {
                g.Add("used_tools", "issued at least one tool call", 5, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.AddToolErrorCheckpoint("clean_tools", "no failed tool calls or invalid world commands", 10,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0,
                    scoresFailedToolCalls: true, scoresInvalidCommands: true,
                    detail: $"{run.FailedToolCalls} failed, {env.World.InvalidCommandCount} invalid");
            }
        }

        private sealed class SpawnArena : WorldBuildScenario
        {
            private static readonly string[] Names = { "Player", "Enemy1", "Enemy2", "Enemy3", "Enemy4" };

            public override string Id => "g1_spawn_arena";
            public override string Name => "Spawn arena";

            public override string WhatItChecks =>
                "Spawns a Player + 4 uniquely-named enemies — checks correct named spawns and exact count.";

            public override IReadOnlyList<string> ExpectedSceneObjectNames => Names;

            protected override bool UsesLua => false;

            public override string Goal =>
                "Set up a small combat arena with the world_command tool: one player and four enemies. Give " +
                "them the clear names 'Player', 'Enemy1', 'Enemy2', 'Enemy3', 'Enemy4', pick whatever " +
                "primitive shape you think best represents each, and place them at distinct positions so they " +
                "don't overlap (centres at least 1 m apart in x/z). Build only what was asked — the player and " +
                "the four enemies, nothing else.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                RecordedScene scene = RecordedScene.From(env.World.Commands);
                bool player = scene.Contains("Player");
                int enemies = 0;
                for (int i = 1; i <= 4; i++)
                {
                    if (scene.Contains($"Enemy{i}"))
                    {
                        enemies++;
                    }
                }

                int distinct = DistinctSpawnCount(env);
                int totalSpawns = env.World.Count("spawn");
                bool separated = PairwiseSeparated(scene, Names, out string spreadDetail);

                g.Add("player", "spawned 'Player'", 20, player, true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("four_enemies", "spawned Enemy1..Enemy4 (unique names)", 45, enemies == 4,
                    dimension: BenchmarkDimension.TaskCompletion, detail: $"{enemies}/4 uniquely-named enemies");
                g.Add("exact_five", "exactly five distinct objects, nothing extra", 20,
                    distinct == 5 && totalSpawns == 5, dimension: BenchmarkDimension.IntentSequence,
                    detail: $"{distinct} distinct / {totalSpawns} spawn commands");
                g.Add("spatial_spread", "the five arena objects stand at least 1 m apart in x/z", 10,
                    separated, dimension: BenchmarkDimension.TaskCompletion, detail: spreadDetail);

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
            private static readonly string[] Names = { "Player", "Coin1", "Coin2", "Coin3", "Goal" };

            public override string Id => "g1_coin_collector";
            public override string Name => "Coin collector";

            public override string WhatItChecks =>
                "Builds a coin-collector (Player/Coins/Goal) and wires score+win Lua — checks world build + rule logic.";

            public override IReadOnlyList<string> ExpectedSceneObjectNames => Names;

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
                "place them at distinct positions (centres at least 1 m apart in x/z) so the layout is " +
                "playable. Spawn only these five objects.\n" +
                "2. With execute_lua, define two logic slots: a 'score_formula' that takes the number of coins " +
                "collected and returns the score (one point per coin), and a 'win_condition' that takes the " +
                "score and returns true once the player has collected at least 3 coins. Use " +
                "logic_define('name', function(...) ... end); work out the bodies yourself.\n" +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                RecordedScene scene = RecordedScene.From(env.World.Commands);
                bool player = scene.Contains("Player");
                int coins = 0;
                for (int i = 1; i <= 3; i++)
                {
                    if (scene.Contains($"Coin{i}"))
                    {
                        coins++;
                    }
                }

                bool goal = scene.Contains("Goal");
                int totalSpawns = env.World.Count("spawn");
                bool separated = PairwiseSeparated(scene, Names, out string spreadDetail);

                // WHY: with only the six small counts a lookup table of the first ten answers passed.
                bool scoreOk = Num(env, "score_formula", 0, 0.0) && Num(env, "score_formula", 1, 1.0)
                                                                 && Num(env, "score_formula", 2, 2.0)
                                                                 && Num(env, "score_formula", 3, 3.0)
                                                                 && Num(env, "score_formula", 5, 5.0)
                                                                 && Num(env, "score_formula", 9, 9.0)
                                                                 && Num(env, "score_formula", 17, 17.0)
                                                                 && Num(env, "score_formula", 100, 100.0);
                bool winBelow = Bool(env, "win_condition", false, 0.0) && Bool(env, "win_condition", false, 1.0)
                                                                       && Bool(env, "win_condition", false, 2.0);
                bool winAt = Bool(env, "win_condition", true, 3.0) && Bool(env, "win_condition", true, 4.0)
                                                                   && Bool(env, "win_condition", true, 9.0)
                                                                   && Bool(env, "win_condition", true, 50.0);

                g.Add("player", "Player spawned", 8, player, dimension: BenchmarkDimension.TaskCompletion);
                g.Add("three_coins", "Coin1..Coin3 spawned (unique)", 15, coins == 3,
                    dimension: BenchmarkDimension.TaskCompletion, detail: $"{coins}/3 coins");
                g.Add("goal", "Goal spawned", 6, goal, dimension: BenchmarkDimension.TaskCompletion);
                g.Add("no_junk", "exactly five objects, nothing extra", 8, totalSpawns == 5,
                    dimension: BenchmarkDimension.IntentSequence, detail: $"{totalSpawns} spawn commands");
                g.Add("spatial_spread", "Player, coins, and Goal stand at least 1 m apart in x/z", 8,
                    separated, dimension: BenchmarkDimension.TaskCompletion, detail: spreadDetail);
                g.Add("score_formula", "score_formula(n)==n on hidden samples", 22, scoreOk, true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("win_below_false", "win_condition false for 0,1,2", 10, winBelow,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("win_at_true", "win_condition true for 3,4,9,50", 16, winAt, true,
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
            private static readonly string[] Names = { "Tree", "Rock", "Bush" };

            public override string Id => "g1_constraint_budget";
            public override string Name => "Constraint budget";

            public override string WhatItChecks =>
                "Spawns exactly Tree/Rock/Bush — checks instruction discipline (no extra or other actions).";

            public override IReadOnlyList<string> ExpectedSceneObjectNames => Names;

            protected override bool UsesLua => false;
            public override int TokenBudget => 1200;

            public override string Goal =>
                "Spawn exactly three objects and do nothing else. Use world_command action='spawn' with distinct " +
                "targetName and a fitting primitive prefabKey: 'Tree' (capsule), 'Rock' (sphere), 'Bush' (sphere). " +
                "Place them at three distinct x/z positions (centres at least 1 m apart). Do not spawn extra " +
                "objects, do not move or destroy anything.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                RecordedScene scene = RecordedScene.From(env.World.Commands);
                bool all = scene.Contains("Tree") && scene.Contains("Rock") && scene.Contains("Bush");
                int totalSpawns = env.World.Count("spawn");
                int otherWorld = env.World.Commands.Count - totalSpawns;
                bool separated = PairwiseSeparated(scene, Names, out string spreadDetail);
                bool prefabs = HasPrefab(scene, "Tree", "capsule") && HasPrefab(scene, "Rock", "sphere")
                                                                    && HasPrefab(scene, "Bush", "sphere");

                g.Add("three_named", "spawned Tree, Rock, Bush", 45, all, true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("exactly_three", "exactly three spawns, no more", 25, totalSpawns == 3,
                    dimension: BenchmarkDimension.IntentSequence, detail: $"{totalSpawns} spawns");
                g.Add("no_other_actions", "no move/destroy/other world actions", 15, otherWorld == 0,
                    dimension: BenchmarkDimension.IntentSequence, detail: $"{otherWorld} other world command(s)");
                g.Add("spatial_spread", "Tree, Rock, and Bush stand at least 1 m apart in x/z", 10,
                    separated, dimension: BenchmarkDimension.TaskCompletion, detail: spreadDetail);
                // WHY: the brief names a primitive per object, and only the names used to be graded.
                g.Add("prefab_keys", "Tree is a capsule, Rock and Bush are spheres", 10, prefabs,
                    dimension: BenchmarkDimension.InstructionAdherence,
                    detail: prefabs ? null : DescribePrefabs(scene));

                if (totalSpawns > 3)
                {
                    g.Penalty("spawned more than three objects", 6 * (totalSpawns - 3));
                }

                if (otherWorld > 0)
                {
                    g.Penalty("used disallowed world actions", 6 * otherWorld);
                }

                if (all && prefabs && totalSpawns == 3 && otherWorld == 0)
                {
                    g.Bonus = 5;
                }

                return g;
            }

            private static bool HasPrefab(RecordedScene scene, string name, string prefab)
            {
                return scene.TryGet(name, out RecordedSceneObject o)
                       && string.Equals(o.Prefab, prefab, StringComparison.OrdinalIgnoreCase);
            }

            private static string DescribePrefabs(RecordedScene scene)
            {
                StringBuilder sb = new();
                foreach (string name in Names)
                {
                    sb.Append(name).Append('=')
                        .Append(scene.TryGet(name, out RecordedSceneObject o) ? o.Prefab : "missing").Append(' ');
                }

                return sb.ToString().TrimEnd();
            }
        }

        /// <summary>
        /// Extreme tier: a 27-object octagonal fortress whose every position, length and yaw is derived from a
        /// coordinate specification (ring of towers, walls between them, gates cut into the walls nearest north
        /// and south, a brazier courtyard mirrored across an oblique axis), plus two logic slots — one that
        /// answers from the specified layout on hidden bearings, one that must read the live world and is
        /// re-checked after the grader moves and removes towers in a reference copy of the fortress.
        /// </summary>
        private sealed class ExtremeFortress : WorldBuildScenario
        {
            private const double CentreX = 6.0;
            private const double CentreZ = -4.0;
            private const double RingRadius = 15.0;
            private const double FirstTowerBearing = 10.0;
            private const double TowerStep = 45.0;
            private const double TowerDiameter = 3.0;
            private const double GateWidth = 3.0;
            private const double MirrorAxisBearing = 10.0;
            private const double TieRatio = 1.01;
            private const string NearestSlot = "nearest_tower";
            private const string BearingSlot = "wall_for_bearing";

            private static readonly List<ExpectedSceneObject> Canonical = BuildCanonical();

            private static readonly string[] TowerNames =
                { "Tower1", "Tower2", "Tower3", "Tower4", "Tower5", "Tower6", "Tower7", "Tower8" };

            private static readonly string[] FullWallNames = { "Wall1", "Wall2", "Wall3", "Wall5", "Wall6", "Wall7" };
            private static readonly string[] GateNames = { "Gate_North", "Gate_South" };
            private static readonly string[] SegmentNames = { "Wall4a", "Wall4b", "Wall8a", "Wall8b" };
            private static readonly string[] BrazierNames = { "Brazier1", "Brazier2", "Brazier3" };
            private static readonly string[] MirroredNames = { "Brazier1M", "Brazier2M", "Brazier3M" };

            private static readonly double[] SectorBearings =
                { 0.0, 10.0, 9.99, 54.99, 55.0, -305.0, 735.0, 100.0, 324.99, 325.0, 190.0, 280.0, 359.999 };

            private static readonly double[] GateBearings =
                { 347.5, 353.2, 354.1, -12.5, 341.8, 340.9, 167.5, 173.2, 174.1, 161.8, 160.9, 527.5 };

            private static readonly string[] StaticNearestSubjects =
            {
                "Keep", "Gate_North", "Gate_South", "Wall4b", "Wall8a", "Brazier1", "Brazier2M", "Brazier3M",
                "Tower6"
            };

            public override string Id => "g1_extreme_fortress";
            public override string Name => "Extreme: Octagon fortress";
            public override bool IsExtreme => true;
            public override int Difficulty => 5;
            public override float TimeoutSeconds => 480f;
            public override int TokenBudget => 16000;
            public override double TimeBudgetMs => 240000;

            // WHY: 27 derived objects plus Lua take far more than the default 40 round trips when a model
            // places objects one call at a time, and a heavy extreme run is not repeated per suite rep.
            public override int? MaxToolCallRoundtripsOverride => 80;
            public override int? RepsOverride => 1;

            public override string WhatItChecks =>
                "Extreme: 27 objects derived from a coordinate spec (ring, chords, gate cuts, oblique mirror) " +
                "plus a bearing slot on hidden inputs and a tower-query slot re-checked after towers move.";

            public override IReadOnlyList<string> ExpectedSceneObjectNames
            {
                get
                {
                    List<string> names = new();
                    foreach (ExpectedSceneObject e in Canonical)
                    {
                        names.Add(e.Name);
                    }

                    return names;
                }
            }

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot(NearestSlot);
                env.Lua.DeclareSlot(BearingSlot);
            }

            public override string Goal =>
                "EXTREME TASK: build an octagonal fortress exactly from this coordinate specification, then " +
                "install two logic slots. Every rule is exact; follow it literally.\n\n" +
                "COORDINATES. Units are metres; +x is east, +z is north, +y is up. The fortress centre is " +
                "C = (x 6, z -4). A bearing is an angle in degrees measured from north (+z) towards east (+x), " +
                "clockwise seen from above: the point at distance d and bearing b from C is " +
                "x = 6 + d*sin(b), z = -4 + d*cos(b). Yaw is the spawn rotation fy (ry in a spawn_batch item): " +
                "an object with yaw w has its local +Z axis along bearing w and its local +X axis along bearing " +
                "w + 90. Leave the x and z rotations at 0 on every object. Tolerances: position 0.05 m, scale " +
                "0.02, yaw 0.5 degrees (a box yaw may also differ by exactly 180). Give values with at least 3 " +
                "decimals.\n\n" +
                "OBJECTS. Create them with world_command spawn or spawn_batch, all at scene root (no parent " +
                "or stringValue, no empty/root helper objects), each name exactly once, nothing else. In a " +
                "spawn_batch give every item its own prefabKey, x/y/z, ry and scaleX/scaleY/scaleZ. Unscaled " +
                "cubes and spheres are 1 m; an unscaled cylinder is 2 m tall and 1 m wide, so a cylinder with " +
                "scaleY s is 2*s m tall.\n" +
                "1. 'Keep': cube at x 6, y 3, z -4, scale 6/6/6, yaw 0.\n" +
                "2. 'Tower1'..'Tower8': cylinders on the circle of radius 15 around C. Tower k stands at bearing " +
                "10 + 45*(k-1). scaleX = scaleZ = 3; scaleY = 5 for odd k and 4 for even k. A tower stands on " +
                "the ground, so its centre y equals its scaleY.\n" +
                "3. Walls: wall k joins Tower k to Tower k+1 (wall 8 joins Tower8 to Tower1). A wall is a cube " +
                "centred on the midpoint (x, z) of its two tower centres, y 2, scaleY 4, scaleZ 1.5, scaleX = " +
                "(distance between the two tower centres) - 3, and yaw = the bearing of the wall centre as seen " +
                "from C. Spawn each wall whole as 'Wall<k>' (e.g. 'Wall1'), except the two gate walls.\n" +
                "4. Gates: the wall whose centre bearing is closest to 0 holds 'Gate_North'; the wall whose " +
                "centre bearing is closest to 180 holds 'Gate_South'. A gate is a cube at its wall's centre " +
                "(x, z), y 2.5, scale 3/5/2.5, with its wall's yaw. A gate wall k is not spawned whole: spawn " +
                "its two remaining pieces 'Wall<k>a' (the piece touching Tower k) and 'Wall<k>b' (the piece " +
                "touching the next tower). Each piece is a cube with the wall's yaw, y 2, scaleY 4, scaleZ 1.5 " +
                "and scaleX = (L - 3) / 2, where L is the scaleX the whole wall would have; it exactly fills " +
                "the gap between the 3 m gate and its tower.\n" +
                "5. 'Brazier1', 'Brazier2', 'Brazier3': spheres, scale 1, y 0.5, at C + (4, 5), C + (6, -2) " +
                "and C + (-1, -7), where C + (dx, dz) means x = 6 + dx, z = -4 + dz.\n" +
                "6. 'Brazier1M', 'Brazier2M', 'Brazier3M': the mirror images of Brazier1..3 across the " +
                "vertical plane through C and Tower1 (the line through C at bearing 10); spheres, scale 1, " +
                "y 0.5.\n" +
                "That is 27 objects.\n\n" +
                "LOGIC. With execute_lua define the pre-declared slots 'nearest_tower' and 'wall_for_bearing' " +
                "via logic_define(name, function(...) ... end). This Lua has the standard string/table/math " +
                "libraries in Lua 5.2 style (math.atan2(y, x) is available) plus " +
                "read-only world queries: coreai_world_pos(name) returns {x=..., y=..., z=...} or nil, " +
                "coreai_world_exists(name), coreai_world_find(pattern). coreai_world_pos answers in Roblox " +
                "studs (x_studs = x_m / 0.28, z_studs = -z_m / 0.28), so compare world positions only with " +
                "each other. Lua cannot create objects here, and execute_lua accepts at most 20 calls a " +
                "minute. One slot call may run at most 200,000 Lua VM instructions; a call that exceeds it or " +
                "raises uninstalls the slot. Every failed tool call (a Lua chunk that raises, a rejected or " +
                "rate-limited call) costs points: check values by returning a string, never by raising. You " +
                "have about 8 minutes for this task.\n" +
                "a) nearest_tower(name) returns a string. Read the CURRENT positions at call time with " +
                "coreai_world_pos: the grader also calls it on a reference copy of this fortress after moving " +
                "and removing towers, so stored or hard-coded positions fail. Return 'none' if the named object " +
                "does not exist or none of 'Tower1'..'Tower8' exists. Use horizontal distance (x and z only) " +
                "to every existing tower; with dmin the smallest distance, return the lowest-numbered tower " +
                "whose distance is at most 1.01 * dmin (a tower counts itself at distance 0, so " +
                "nearest_tower('Tower6') = 'Tower6').\n" +
                "b) wall_for_bearing(b) returns a string computed from the specified geometry above (no world " +
                "reads); b is a bearing in degrees, any real number. First " +
                "reduce b into [0, 360). If the ray from C at bearing b passes through a gate opening, return " +
                "that gate's name: that is when the short-way angular difference between b and the gate's yaw " +
                "is at most atan(1.5 / D) in degrees, D being the distance from C to that gate's centre (edges " +
                "included). Otherwise return 'Wall' followed by the k whose sector holds b: bearing of Tower k " +
                "<= b < bearing of Tower k + 45, wrapping past 360 (so 0 and 9.9 belong to 'Wall8'). A gate " +
                "wall's sector still answers 'Wall<k>' outside its gate opening.\n" +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                RecordedScene scene = RecordedScene.From(env.World.Commands);
                int invalid = env.World.InvalidCommandCount;
                int luaFailures = env.Lua.FailedExecutions;
                g.AddToolErrorCheckpoint("clean_tools", "no failed tool calls, invalid world commands or failed Lua",
                    4, run.FailedToolCalls == 0 && invalid == 0 && luaFailures == 0,
                    scoresFailedToolCalls: true, scoresInvalidCommands: true,
                    detail: $"{run.FailedToolCalls} failed, {invalid} invalid, {luaFailures} Lua failures");

                AddFamily(g, scene, "keep", "Keep cube at C, 6 m cube, yaw 0", 3, new[] { "Keep" },
                    SceneAspect.All, BenchmarkDimension.TaskCompletion);
                AddFamily(g, scene, "towers_xz", "Tower1..Tower8 on the radius-15 ring at their bearings", 10,
                    TowerNames, SceneAspect.PositionXZ, BenchmarkDimension.Reasoning, true);
                AddFamily(g, scene, "towers_height", "towers are 3 m cylinders, alternating 10 m / 8 m, grounded",
                    5, TowerNames, SceneAspect.PositionY | SceneAspect.Shape | SceneAspect.Rotation,
                    BenchmarkDimension.Reasoning);
                AddFamily(g, scene, "walls_center", "six whole walls centred between their towers", 7,
                    FullWallNames, SceneAspect.PositionXZ | SceneAspect.PositionY, BenchmarkDimension.Reasoning);
                AddFamily(g, scene, "walls_size_yaw", "whole walls: chord - 3 long, yaw = centre bearing", 7,
                    FullWallNames, SceneAspect.Shape | SceneAspect.Rotation, BenchmarkDimension.Reasoning);
                AddFamily(g, scene, "gates", "Gate_North / Gate_South cut into the walls nearest 0 and 180", 7,
                    GateNames, SceneAspect.All, BenchmarkDimension.Reasoning);
                AddFamily(g, scene, "gate_wall_segments", "Wall4a/4b/8a/8b fill each gate wall beside its gate",
                    10, SegmentNames, SceneAspect.All, BenchmarkDimension.Reasoning);
                AddFamily(g, scene, "braziers", "Brazier1..3 at their stated offsets", 3, BrazierNames,
                    SceneAspect.All, BenchmarkDimension.TaskCompletion);
                AddFamily(g, scene, "mirrored_braziers", "Brazier1M..3M mirrored across the bearing-10 axis", 7,
                    MirroredNames, SceneAspect.All, BenchmarkDimension.Reasoning);

                InventoryReport inventory = InventoryReport.Compare(scene, Canonical);
                g.Add("exact_inventory", "exactly the 27 specified objects, each once, at scene root", 5,
                    inventory.Exact, dimension: BenchmarkDimension.InstructionAdherence,
                    detail: inventory.Describe());

                bool installed = env.Lua.LogicSlots.IsOverridden(NearestSlot)
                                 && env.Lua.LogicSlots.IsOverridden(BearingSlot);
                g.Add("slots_installed", "nearest_tower and wall_for_bearing installed", 2, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);

                SlotSamples sectors = new(env);
                foreach (double bearing in SectorBearings)
                {
                    sectors.String(BearingSlot, ExpectedWallForBearing(bearing), bearing);
                }

                SlotSamples gates = new(env);
                foreach (double bearing in GateBearings)
                {
                    gates.String(BearingSlot, ExpectedWallForBearing(bearing), bearing);
                }

                int baseline = env.World.Commands.Count;
                Dictionary<string, PlanPosition> world = ExpectedSceneObject.PlanMap(Canonical);
                SlotSamples nearestStatic = new(env);
                SlotSamples nearestLive = new(env);
                try
                {
                    VirtualScene.Load(env, Canonical);
                    foreach (string subject in StaticNearestSubjects)
                    {
                        nearestStatic.String(NearestSlot, NearestTower(world, subject), subject);
                    }

                    ProbeMovedTower(env, world, nearestLive);
                    ProbeRemovedTower(env, world, nearestLive);

                    // WHY: a slot that throws is uninstalled, so the input most likely to crash it runs last and
                    // cannot hide the outcome of the other samples.
                    nearestStatic.String(NearestSlot, NearestTower(world, "Moat"), "Moat");
                }
                finally
                {
                    VirtualScene.Restore(env, scene, baseline);
                }

                // WHY: after the reference probes, because a slot that raises on the model's own world is
                // uninstalled and must not also forfeit the reference checkpoints.
                SlotSamples own = OwnWorldSamples(env, scene);

                g.Add("bearing_sectors", "wall_for_bearing: reduction into [0,360) and sector edges", 6,
                    sectors.AllPassed, dimension: BenchmarkDimension.Reasoning, detail: sectors.Detail);
                g.Add("bearing_gates", "wall_for_bearing: gate openings of atan(1.5/D) on hidden bearings", 6,
                    gates.AllPassed, dimension: BenchmarkDimension.Reasoning, detail: gates.Detail);
                g.Add("nearest_tower_static", "nearest_tower on the reference fortress, ties included", 6,
                    nearestStatic.AllPassed, dimension: BenchmarkDimension.TaskCompletion,
                    detail: nearestStatic.Detail);
                g.Add("nearest_tower_live", "nearest_tower follows a moved and a removed tower", 8,
                    nearestLive.AllPassed, true, nearestLive.Detail, BenchmarkDimension.Reasoning);
                g.Add("own_world_consistency", "nearest_tower agrees with the fortress the model built", 4,
                    own.AllPassed, dimension: BenchmarkDimension.Reasoning, detail: own.Detail);

                if (inventory.Extra.Count > 0)
                {
                    g.Penalty($"spawned {inventory.Extra.Count} object(s) outside the specification",
                        3 * inventory.Extra.Count);
                }

                if (scene.DuplicateSpawns > 0)
                {
                    g.Penalty($"spawned {scene.DuplicateSpawns} duplicate object(s)", 3 * scene.DuplicateSpawns);
                }

                if (AllPassed(g) && g.Penalties.Count == 0)
                {
                    g.Bonus = 8;
                }

                if (run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                return g;
            }

            private static void AddFamily(ScenarioGrading g, RecordedScene scene, string id, string description,
                double weight, IReadOnlyList<string> names, SceneAspect aspects, BenchmarkDimension dimension,
                bool mandatory = false)
            {
                bool passed = ExpectedSceneObject.CheckFamily(scene, Canonical, names, aspects, out string detail);
                g.Add(id, description, weight, passed, mandatory, detail, dimension);
            }

            private static bool AllPassed(ScenarioGrading g)
            {
                foreach (BenchmarkCheckpoint checkpoint in g.Checkpoints)
                {
                    if (!checkpoint.Passed)
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>
            /// The model's slot against the model's own fortress: each tower must answer itself and the Keep
            /// and gates whatever the built positions imply. Needs the towers and the Keep to exist.
            /// </summary>
            private static SlotSamples OwnWorldSamples(BenchmarkEnvironment env, RecordedScene scene)
            {
                SlotSamples own = new(env);
                List<string> missing = new();
                foreach (string name in TowerNames)
                {
                    if (!scene.Contains(name))
                    {
                        missing.Add(name);
                    }
                }

                if (!scene.Contains("Keep"))
                {
                    missing.Add("Keep");
                }

                if (missing.Count > 0)
                {
                    own.Fail("built fortress lacks " + string.Join(", ", missing));
                    return own;
                }

                Dictionary<string, PlanPosition> world = scene.PlanMap();
                foreach (string name in TowerNames)
                {
                    own.String(NearestSlot, NearestTower(world, name), name);
                }

                foreach (string name in new[] { "Keep", "Gate_North", "Gate_South", "Brazier1M" })
                {
                    if (scene.Contains(name))
                    {
                        own.String(NearestSlot, NearestTower(world, name), name);
                    }
                }

                return own;
            }

            private static void ProbeMovedTower(BenchmarkEnvironment env, Dictionary<string, PlanPosition> world,
                SlotSamples live)
            {
                ExpectedSceneObject tower6 = Find("Tower6");
                ExpectedSceneObject brazier1 = Find("Brazier1");
                double x = brazier1.X + 0.5;
                double z = brazier1.Z;
                VirtualScene.Move(env, tower6.Name, x, tower6.Y, z);
                world[tower6.Name] = PlanPosition.Of(x, z);
                live.String(NearestSlot, NearestTower(world, "Brazier1"), "Brazier1");
                live.String(NearestSlot, NearestTower(world, "Keep"), "Keep");
                VirtualScene.Move(env, tower6.Name, tower6.X, tower6.Y, tower6.Z);
                world[tower6.Name] = PlanPosition.Of(tower6.X, tower6.Z);
            }

            private static void ProbeRemovedTower(BenchmarkEnvironment env, Dictionary<string, PlanPosition> world,
                SlotSamples live)
            {
                ExpectedSceneObject tower1 = Find("Tower1");
                VirtualScene.Destroy(env, tower1.Name);
                world.Remove(tower1.Name);
                live.String(NearestSlot, NearestTower(world, "Keep"), "Keep");
                live.String(NearestSlot, NearestTower(world, "Gate_North"), "Gate_North");
                live.String(NearestSlot, NearestTower(world, "Wall8b"), "Wall8b");
                VirtualScene.Spawn(env, tower1);
                world[tower1.Name] = PlanPosition.Of(tower1.X, tower1.Z);
            }

            private static ExpectedSceneObject Find(string name)
            {
                foreach (ExpectedSceneObject e in Canonical)
                {
                    if (string.Equals(e.Name, name, StringComparison.Ordinal))
                    {
                        return e;
                    }
                }

                throw new InvalidOperationException($"'{name}' is not part of the fortress specification.");
            }

            private static string NearestTower(Dictionary<string, PlanPosition> world, string subject)
            {
                return PlanPosition.NearestCandidate(world, subject, TowerNames, TieRatio);
            }

            /// <summary>Reference answer of <c>wall_for_bearing</c>, computed from the specified geometry.</summary>
            private static string ExpectedWallForBearing(double bearing)
            {
                double b = bearing - 360.0 * Math.Floor(bearing / 360.0);
                foreach (string gate in GateNames)
                {
                    ExpectedSceneObject e = Find(gate);
                    double distance = Math.Sqrt((e.X - CentreX) * (e.X - CentreX) + (e.Z - CentreZ) * (e.Z - CentreZ));
                    double halfAngle = Math.Atan(GateWidth / 2.0 / distance) * 180.0 / Math.PI;
                    if (ExpectedSceneObject.AngleOff(b, e.Yaw, 360.0) <= halfAngle)
                    {
                        return gate;
                    }
                }

                for (int k = 1; k <= 8; k++)
                {
                    double start = FirstTowerBearing + TowerStep * (k - 1);
                    double into = b - start - 360.0 * Math.Floor((b - start) / 360.0);
                    if (into < TowerStep)
                    {
                        return "Wall" + k;
                    }
                }

                return "none";
            }

            private static List<ExpectedSceneObject> BuildCanonical()
            {
                List<ExpectedSceneObject> list = new()
                {
                    ExpectedSceneObject.Box("Keep", "cube", CentreX, 3.0, CentreZ, 6.0, 6.0, 6.0, 0.0, 90.0)
                };

                double[] towerX = new double[9];
                double[] towerZ = new double[9];
                for (int k = 1; k <= 8; k++)
                {
                    double bearing = (FirstTowerBearing + TowerStep * (k - 1)) * Math.PI / 180.0;
                    towerX[k] = CentreX + RingRadius * Math.Sin(bearing);
                    towerZ[k] = CentreZ + RingRadius * Math.Cos(bearing);
                    double height = k % 2 == 1 ? 5.0 : 4.0;
                    list.Add(ExpectedSceneObject.Upright($"Tower{k}", "cylinder", towerX[k], height, towerZ[k],
                        TowerDiameter, height, TowerDiameter));
                }

                int northWall = 0;
                int southWall = 0;
                double bestNorth = double.MaxValue;
                double bestSouth = double.MaxValue;
                double[] midX = new double[9];
                double[] midZ = new double[9];
                double[] yaw = new double[9];
                for (int k = 1; k <= 8; k++)
                {
                    int next = k % 8 + 1;
                    midX[k] = (towerX[k] + towerX[next]) / 2.0;
                    midZ[k] = (towerZ[k] + towerZ[next]) / 2.0;
                    yaw[k] = Bearing(midX[k] - CentreX, midZ[k] - CentreZ);
                    double offNorth = ExpectedSceneObject.AngleOff(yaw[k], 0.0, 360.0);
                    double offSouth = ExpectedSceneObject.AngleOff(yaw[k], 180.0, 360.0);
                    if (offNorth < bestNorth)
                    {
                        bestNorth = offNorth;
                        northWall = k;
                    }

                    if (offSouth < bestSouth)
                    {
                        bestSouth = offSouth;
                        southWall = k;
                    }
                }

                for (int k = 1; k <= 8; k++)
                {
                    int next = k % 8 + 1;
                    double dx = towerX[next] - towerX[k];
                    double dz = towerZ[next] - towerZ[k];
                    double chord = Math.Sqrt(dx * dx + dz * dz);
                    double length = chord - TowerDiameter;
                    if (k != northWall && k != southWall)
                    {
                        list.Add(ExpectedSceneObject.Box($"Wall{k}", "cube", midX[k], 2.0, midZ[k], length, 4.0,
                            1.5, yaw[k], 180.0));
                        continue;
                    }

                    string gate = k == northWall ? "Gate_North" : "Gate_South";
                    list.Add(ExpectedSceneObject.Box(gate, "cube", midX[k], 2.5, midZ[k], 3.0, 5.0, 2.5, yaw[k],
                        180.0));
                    double piece = (length - GateWidth) / 2.0;
                    double offset = GateWidth / 2.0 + piece / 2.0;
                    double ux = dx / chord;
                    double uz = dz / chord;
                    list.Add(ExpectedSceneObject.Box($"Wall{k}a", "cube", midX[k] - ux * offset, 2.0,
                        midZ[k] - uz * offset, piece, 4.0, 1.5, yaw[k], 180.0));
                    list.Add(ExpectedSceneObject.Box($"Wall{k}b", "cube", midX[k] + ux * offset, 2.0,
                        midZ[k] + uz * offset, piece, 4.0, 1.5, yaw[k], 180.0));
                }

                double[,] offsets = { { 4.0, 5.0 }, { 6.0, -2.0 }, { -1.0, -7.0 } };
                double axisX = Math.Sin(MirrorAxisBearing * Math.PI / 180.0);
                double axisZ = Math.Cos(MirrorAxisBearing * Math.PI / 180.0);
                for (int i = 0; i < 3; i++)
                {
                    double vx = offsets[i, 0];
                    double vz = offsets[i, 1];
                    list.Add(ExpectedSceneObject.Ball($"Brazier{i + 1}", CentreX + vx, 0.5, CentreZ + vz, 1.0));
                    double dot = vx * axisX + vz * axisZ;
                    list.Add(ExpectedSceneObject.Ball($"Brazier{i + 1}M", CentreX + 2.0 * dot * axisX - vx, 0.5,
                        CentreZ + 2.0 * dot * axisZ - vz, 1.0));
                }

                return list;
            }

            private static double Bearing(double dx, double dz)
            {
                double degrees = Math.Atan2(dx, dz) * 180.0 / Math.PI;
                return degrees < 0.0 ? degrees + 360.0 : degrees;
            }
        }
    }

    /// <summary>Horizontal (x/z) position of a world object, in metres.</summary>
    internal readonly struct PlanPosition
    {
        public PlanPosition(double x, double z)
        {
            X = x;
            Z = z;
        }

        public double X { get; }
        public double Z { get; }

        public static PlanPosition Of(double x, double z)
        {
            // WHY: the recorder keeps world positions as Unity floats, which is what Lua reads back.
            return new PlanPosition((float)x, (float)z);
        }

        /// <summary>
        /// The first of <paramref name="candidates"/> (in the given order) whose horizontal distance to
        /// <paramref name="subject"/> is at most <paramref name="tieRatio"/> times the smallest one; "none"
        /// when the subject or every candidate is missing. Ratio ties make the answer independent of units
        /// and of sub-millimetre rounding.
        /// </summary>
        public static string NearestCandidate(Dictionary<string, PlanPosition> world, string subject,
            IReadOnlyList<string> candidates, double tieRatio)
        {
            if (string.IsNullOrWhiteSpace(subject) || !world.TryGetValue(subject.Trim(), out PlanPosition p))
            {
                return "none";
            }

            double[] distances = new double[candidates.Count];
            double best = double.MaxValue;
            bool any = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (world.TryGetValue(candidates[i], out PlanPosition c))
                {
                    double dx = p.X - c.X;
                    double dz = p.Z - c.Z;
                    distances[i] = Math.Sqrt(dx * dx + dz * dz);
                    best = Math.Min(best, distances[i]);
                    any = true;
                }
                else
                {
                    distances[i] = double.NaN;
                }
            }

            if (!any)
            {
                return "none";
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (!double.IsNaN(distances[i]) && distances[i] <= best * tieRatio)
                {
                    return candidates[i];
                }
            }

            return "none";
        }
    }

    /// <summary>One object alive at the end of a recorded world-command stream.</summary>
    internal sealed class RecordedSceneObject
    {
        public string Name = "";
        public string Prefab = "";
        public string Parent = "";
        public double X, Y, Z;
        public double Rx, Ry, Rz;
        public double Sx = 1.0, Sy = 1.0, Sz = 1.0;
    }

    /// <summary>
    /// The objects a recorded world-command stream leaves alive and where they ended up: positions follow the
    /// recording executor's virtual scene (the one Lua's world queries read), scale and rotation follow the
    /// production executor. Graders score this final state, so an object fixed with a later <c>change</c>
    /// counts and one destroyed afterwards does not.
    /// </summary>
    internal sealed class RecordedScene
    {
        private readonly Dictionary<string, RecordedSceneObject> _alive = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new();

        /// <summary>Spawns of a name that was still alive at the time.</summary>
        public int DuplicateSpawns { get; private set; }

        public IEnumerable<RecordedSceneObject> Objects
        {
            get
            {
                foreach (string name in _order)
                {
                    if (_alive.TryGetValue(name, out RecordedSceneObject o))
                    {
                        yield return o;
                    }
                }
            }
        }

        public static RecordedScene From(IEnumerable<RecordedWorldCommand> commands)
        {
            RecordedScene scene = new();
            foreach (RecordedWorldCommand c in commands)
            {
                scene.Apply(c);
            }

            return scene;
        }

        public bool Contains(string name)
        {
            return TryGet(name, out _);
        }

        public bool TryGet(string name, out RecordedSceneObject found)
        {
            return _alive.TryGetValue((name ?? "").Trim(), out found);
        }

        public Dictionary<string, PlanPosition> PlanMap()
        {
            Dictionary<string, PlanPosition> map = new(StringComparer.OrdinalIgnoreCase);
            foreach (RecordedSceneObject o in Objects)
            {
                map[o.Name] = PlanPosition.Of(o.X, o.Z);
            }

            return map;
        }

        private void Apply(RecordedWorldCommand c)
        {
            string name = (c.TargetName ?? "").Trim();
            if (name.Length == 0)
            {
                return;
            }

            string action = (c.Action ?? "").Trim().ToLowerInvariant();
            if (action == "spawn")
            {
                if (_alive.ContainsKey(name))
                {
                    DuplicateSpawns++;
                }
                else
                {
                    _order.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    _order.Add(name);
                }

                RecordedSceneObject spawned = new()
                {
                    Name = name,
                    Prefab = (c.PrefabKeyOrName ?? "").Trim(),
                    Parent = ParentName(c.StringValue),
                    X = c.X, Y = c.Y, Z = c.Z,
                    Rx = c.Fx, Ry = c.Fy, Rz = c.Fz
                };
                SetScale(spawned, c, false);
                _alive[name] = spawned;
                return;
            }

            if (!_alive.TryGetValue(name, out RecordedSceneObject o))
            {
                return;
            }

            switch (action)
            {
                case "destroy":
                    _alive.Remove(name);
                    break;
                case "move":
                    o.X = c.X;
                    o.Y = c.Y;
                    o.Z = c.Z;
                    break;
                case "set_transform":
                    o.X = c.X;
                    o.Y = c.Y;
                    o.Z = c.Z;
                    o.Rx = c.Fx;
                    o.Ry = c.Fy;
                    o.Rz = c.Fz;
                    SetScale(o, c, false);
                    break;
                case "set_scale":
                    SetScale(o, c, false);
                    break;
                case "parent":
                    o.Parent = ParentName(c.StringValue);
                    break;
                case "change":
                    ApplyChange(o, c);
                    break;
            }
        }

        private static void ApplyChange(RecordedSceneObject o, RecordedWorldCommand c)
        {
            if (c.HasPosition && !c.HasX && !c.HasY && !c.HasZ)
            {
                o.X = c.X;
                o.Y = c.Y;
                o.Z = c.Z;
            }
            else
            {
                o.X = c.HasX ? c.X : o.X;
                o.Y = c.HasY ? c.Y : o.Y;
                o.Z = c.HasZ ? c.Z : o.Z;
            }

            if (c.HasRotation && !c.HasFx && !c.HasFy && !c.HasFz)
            {
                o.Rx = c.Fx;
                o.Ry = c.Fy;
                o.Rz = c.Fz;
            }
            else
            {
                o.Rx = c.HasFx ? c.Fx : o.Rx;
                o.Ry = c.HasFy ? c.Fy : o.Ry;
                o.Rz = c.HasFz ? c.Fz : o.Rz;
            }

            if (c.HasScale)
            {
                SetScale(o, c, true);
            }

            if (!string.IsNullOrWhiteSpace(c.StringValue))
            {
                o.Parent = ParentName(c.StringValue);
            }
        }

        /// <summary>The production executor's scale rule: per-axis values win over the uniform one.</summary>
        private static void SetScale(RecordedSceneObject o, RecordedWorldCommand c, bool keepUnsetAxes)
        {
            double uniform = c.FloatValue > 0f ? Clamp(c.FloatValue) : 1.0;
            if (keepUnsetAxes)
            {
                if (c.FloatValue > 0f)
                {
                    o.Sx = o.Sy = o.Sz = uniform;
                }

                o.Sx = c.ScaleX > 0f ? Clamp(c.ScaleX) : o.Sx;
                o.Sy = c.ScaleY > 0f ? Clamp(c.ScaleY) : o.Sy;
                o.Sz = c.ScaleZ > 0f ? Clamp(c.ScaleZ) : o.Sz;
                return;
            }

            o.Sx = c.ScaleX > 0f ? Clamp(c.ScaleX) : uniform;
            o.Sy = c.ScaleY > 0f ? Clamp(c.ScaleY) : uniform;
            o.Sz = c.ScaleZ > 0f ? Clamp(c.ScaleZ) : uniform;
        }

        private static double Clamp(float value)
        {
            return Math.Min(Math.Max(value, 0.01f), 100f);
        }

        private static string ParentName(string value)
        {
            string parent = (value ?? "").Trim();
            return parent.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : parent;
        }
    }

    /// <summary>Which properties of an object an expected-object check compares.</summary>
    [Flags]
    internal enum SceneAspect
    {
        None = 0,
        PositionXZ = 1,
        PositionY = 2,
        Shape = 4,
        Rotation = 8,
        Root = 16,
        All = PositionXZ | PositionY | Shape | Rotation | Root
    }

    /// <summary>
    /// One object a specification requires, with the tolerances every extreme world grader states in its
    /// brief: 0.05 m on positions, 0.02 on scales, 0.5 degrees on yaw.
    /// </summary>
    internal sealed class ExpectedSceneObject
    {
        public const double PositionTolerance = 0.05;
        public const double ScaleTolerance = 0.02;
        public const double AngleTolerance = 0.5;

        public string Name = "";
        public string Prefab = "";
        public double X, Y, Z;
        public double Sx = 1.0, Sy = 1.0, Sz = 1.0;

        /// <summary>Required yaw, compared modulo <see cref="YawPeriod"/>; a period of 0 leaves yaw free.</summary>
        public double Yaw;

        public double YawPeriod;

        /// <summary>True for a sphere, whose orientation is invisible.</summary>
        public bool AnyRotation;

        public static ExpectedSceneObject Box(string name, string prefab, double x, double y, double z,
            double sx, double sy, double sz, double yaw, double yawPeriod)
        {
            return new ExpectedSceneObject
            {
                Name = name, Prefab = prefab, X = x, Y = y, Z = z, Sx = sx, Sy = sy, Sz = sz, Yaw = yaw,
                YawPeriod = yawPeriod
            };
        }

        /// <summary>A shape symmetric about its vertical axis: yaw is free, x/z rotation must stay 0.</summary>
        public static ExpectedSceneObject Upright(string name, string prefab, double x, double y, double z,
            double sx, double sy, double sz)
        {
            return new ExpectedSceneObject
            {
                Name = name, Prefab = prefab, X = x, Y = y, Z = z, Sx = sx, Sy = sy, Sz = sz
            };
        }

        public static ExpectedSceneObject Ball(string name, double x, double y, double z, double scale)
        {
            return new ExpectedSceneObject
            {
                Name = name, Prefab = "sphere", X = x, Y = y, Z = z, Sx = scale, Sy = scale, Sz = scale,
                AnyRotation = true
            };
        }

        public static Dictionary<string, PlanPosition> PlanMap(IEnumerable<ExpectedSceneObject> objects)
        {
            Dictionary<string, PlanPosition> map = new(StringComparer.OrdinalIgnoreCase);
            foreach (ExpectedSceneObject e in objects)
            {
                map[e.Name] = PlanPosition.Of(e.X, e.Z);
            }

            return map;
        }

        /// <summary>Smallest absolute difference between two angles taken modulo <paramref name="period"/>.</summary>
        public static double AngleOff(double a, double b, double period)
        {
            double d = (a - b) % period;
            if (d < 0)
            {
                d += period;
            }

            return Math.Min(d, period - d);
        }

        /// <summary>The first way <paramref name="o"/> misses this object on the given aspects, or null.</summary>
        public string Problem(RecordedSceneObject o, SceneAspect aspects)
        {
            if (o == null)
            {
                return $"{Name} missing";
            }

            if ((aspects & SceneAspect.PositionXZ) != 0 &&
                (Math.Abs(o.X - X) > PositionTolerance || Math.Abs(o.Z - Z) > PositionTolerance))
            {
                return Format("{0} at x/z {1:0.###}/{2:0.###}, want {3:0.###}/{4:0.###}", Name, o.X, o.Z, X, Z);
            }

            if ((aspects & SceneAspect.PositionY) != 0 && Math.Abs(o.Y - Y) > PositionTolerance)
            {
                return Format("{0} at y {1:0.###}, want {2:0.###}", Name, o.Y, Y);
            }

            if ((aspects & SceneAspect.Shape) != 0)
            {
                if (!string.Equals(o.Prefab, Prefab, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{Name} is '{o.Prefab}', want '{Prefab}'";
                }

                if (Math.Abs(o.Sx - Sx) > ScaleTolerance || Math.Abs(o.Sy - Sy) > ScaleTolerance ||
                    Math.Abs(o.Sz - Sz) > ScaleTolerance)
                {
                    return Format("{0} scale {1:0.###}/{2:0.###}/{3:0.###}, want {4:0.###}/{5:0.###}/{6:0.###}",
                        Name, o.Sx, o.Sy, o.Sz, Sx, Sy, Sz);
                }
            }

            if ((aspects & SceneAspect.Rotation) != 0 && !AnyRotation)
            {
                if (AngleOff(o.Rx, 0.0, 360.0) > AngleTolerance || AngleOff(o.Rz, 0.0, 360.0) > AngleTolerance)
                {
                    return Format("{0} tilted fx/fz {1:0.#}/{2:0.#}", Name, o.Rx, o.Rz);
                }

                if (YawPeriod > 0.0 && AngleOff(o.Ry, Yaw, YawPeriod) > AngleTolerance)
                {
                    return Format("{0} yaw {1:0.##}, want {2:0.##}", Name, o.Ry, Yaw);
                }
            }

            if ((aspects & SceneAspect.Root) != 0 && o.Parent.Length > 0)
            {
                return $"{Name} parented to '{o.Parent}'";
            }

            return null;
        }

        /// <summary>
        /// True when every object of <paramref name="names"/> exists and matches its specification on
        /// <paramref name="aspects"/>; the detail names how many matched and the first misses.
        /// </summary>
        public static bool CheckFamily(RecordedScene scene, IReadOnlyList<ExpectedSceneObject> specification,
            IReadOnlyList<string> names, SceneAspect aspects, out string detail)
        {
            List<string> problems = new();
            foreach (string name in names)
            {
                ExpectedSceneObject expected = null;
                foreach (ExpectedSceneObject e in specification)
                {
                    if (string.Equals(e.Name, name, StringComparison.Ordinal))
                    {
                        expected = e;
                        break;
                    }
                }

                if (expected == null)
                {
                    throw new InvalidOperationException($"'{name}' is not in the specification.");
                }

                scene.TryGet(name, out RecordedSceneObject actual);
                string problem = expected.Problem(actual, aspects);
                if (problem != null)
                {
                    problems.Add(problem);
                }
            }

            if (problems.Count == 0)
            {
                detail = null;
                return true;
            }

            detail = $"{names.Count - problems.Count}/{names.Count} ok; " +
                     string.Join("; ", problems.GetRange(0, Math.Min(3, problems.Count)));
            return false;
        }

        private static string Format(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
    }

    /// <summary>How the alive objects of a scene compare with a specification's object list.</summary>
    internal sealed class InventoryReport
    {
        public readonly List<string> Missing = new();
        public readonly List<string> Extra = new();
        public int Parented;
        public int Duplicates;

        public bool Exact => Missing.Count == 0 && Extra.Count == 0 && Parented == 0 && Duplicates == 0;

        public string Describe()
        {
            if (Exact)
            {
                return null;
            }

            return $"{Missing.Count} missing ({string.Join(", ", Missing.GetRange(0, Math.Min(4, Missing.Count)))}), " +
                   $"{Extra.Count} extra ({string.Join(", ", Extra.GetRange(0, Math.Min(4, Extra.Count)))}), " +
                   $"{Duplicates} duplicate spawn(s), {Parented} parented";
        }

        public static InventoryReport Compare(RecordedScene scene, IEnumerable<ExpectedSceneObject> specification)
        {
            InventoryReport report = new() { Duplicates = scene.DuplicateSpawns };
            HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase);
            foreach (ExpectedSceneObject e in specification)
            {
                expected.Add(e.Name);
                if (!scene.Contains(e.Name))
                {
                    report.Missing.Add(e.Name);
                }
            }

            foreach (RecordedSceneObject o in scene.Objects)
            {
                if (!expected.Contains(o.Name))
                {
                    report.Extra.Add(o.Name);
                }
                else if (o.Parent.Length > 0)
                {
                    report.Parented++;
                }
            }

            return report;
        }
    }

    /// <summary>
    /// Hidden-sample bookkeeping for logic slots: every sample is invoked, compared exactly, and the first
    /// misses are kept for the checkpoint detail.
    /// </summary>
    internal sealed class SlotSamples
    {
        private readonly BenchmarkEnvironment _env;
        private readonly List<string> _misses = new();

        public SlotSamples(BenchmarkEnvironment env)
        {
            _env = env;
        }

        public int Total { get; private set; }
        public int Passed { get; private set; }

        public bool AllPassed => Total > 0 && Passed == Total;

        public string Detail => AllPassed
            ? null
            : $"{Passed}/{Total} samples; " + string.Join("; ", _misses.GetRange(0, Math.Min(4, _misses.Count)));

        public void String(string slot, string expected, params object[] args)
        {
            bool ok = _env.Lua.TryString(slot, out string got, args);
            Record(ok && string.Equals(got, expected, StringComparison.Ordinal),
                $"{Call(slot, args)}={(ok ? "'" + got + "'" : "no string")}, want '{expected}'");
        }

        public void Bool(string slot, bool expected, params object[] args)
        {
            bool ok = _env.Lua.TryBool(slot, out bool got, args);
            Record(ok && got == expected,
                $"{Call(slot, args)}={(ok ? got.ToString() : "no boolean")}, want {expected}");
        }

        public void Number(string slot, double expected, params object[] args)
        {
            bool ok = _env.Lua.TryNumber(slot, out double got, args);
            Record(ok && Math.Abs(got - expected) < 1e-9,
                $"{Call(slot, args)}={(ok ? got.ToString(CultureInfo.InvariantCulture) : "no number")}, " +
                $"want {expected.ToString(CultureInfo.InvariantCulture)}");
        }

        /// <summary>Records a sample that could not be run, so the checkpoint fails with the reason.</summary>
        public void Fail(string reason)
        {
            Record(false, reason);
        }

        private void Record(bool passed, string description)
        {
            Total++;
            if (passed)
            {
                Passed++;
            }
            else
            {
                _misses.Add(description);
            }
        }

        private static string Call(string slot, object[] args)
        {
            StringBuilder sb = new(slot);
            sb.Append('(');
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(args[i] is string s
                    ? "'" + s + "'"
                    : Convert.ToString(args[i], CultureInfo.InvariantCulture));
            }

            return sb.Append(')').ToString();
        }
    }

    /// <summary>
    /// Grader-side edits of the recording executor's virtual scene (the world Lua's queries read). Every edit
    /// goes through <see cref="RecordingWorldExecutor.TryExecute"/> as an ordinary valid command, and
    /// <see cref="Restore"/> puts the model's own objects back and drops the grader's commands from the record.
    /// </summary>
    internal static class VirtualScene
    {
        public static void Spawn(BenchmarkEnvironment env, ExpectedSceneObject e)
        {
            Spawn(env, e.Name, e.Prefab, e.X, e.Y, e.Z);
        }

        public static void Spawn(BenchmarkEnvironment env, string name, string prefab, double x, double y, double z)
        {
            Send(env, CoreAiWorldCommandEnvelope.Spawn(prefab ?? "", name, new Vector3((float)x, (float)y, (float)z)));
        }

        public static void Move(BenchmarkEnvironment env, string name, double x, double y, double z)
        {
            Send(env, CoreAiWorldCommandEnvelope.Move(name, new Vector3((float)x, (float)y, (float)z)));
        }

        public static void Destroy(BenchmarkEnvironment env, string name)
        {
            Send(env, CoreAiWorldCommandEnvelope.Destroy(name));
        }

        /// <summary>Removes every object from the virtual scene, then spawns <paramref name="objects"/>.</summary>
        public static void Load(BenchmarkEnvironment env, IEnumerable<ExpectedSceneObject> objects)
        {
            Clear(env);
            foreach (ExpectedSceneObject e in objects)
            {
                Spawn(env, e);
            }
        }

        /// <summary>
        /// Rebuilds the model's final scene and truncates the command record to <paramref name="baseline"/>, so
        /// the grader leaves the environment as the model left it.
        /// </summary>
        public static void Restore(BenchmarkEnvironment env, RecordedScene modelScene, int baseline)
        {
            Clear(env);
            foreach (RecordedSceneObject o in modelScene.Objects)
            {
                Spawn(env, o.Name, o.Prefab, o.X, o.Y, o.Z);
            }

            if (env.World.Commands.Count > baseline)
            {
                env.World.Commands.RemoveRange(baseline, env.World.Commands.Count - baseline);
            }
        }

        private static void Clear(BenchmarkEnvironment env)
        {
            for (int pass = 0; pass < 64; pass++)
            {
                List<object> names = env.World.FindObjectNames("");
                if (names.Count == 0)
                {
                    return;
                }

                foreach (object name in names)
                {
                    Destroy(env, name as string);
                }
            }
        }

        private static void Send(BenchmarkEnvironment env, CoreAiWorldCommandEnvelope envelope)
        {
            env.World.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = Newtonsoft.Json.JsonConvert.SerializeObject(envelope)
            });
        }
    }
}
#endif
#endif
