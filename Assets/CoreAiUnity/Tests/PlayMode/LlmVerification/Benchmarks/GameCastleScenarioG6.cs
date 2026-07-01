#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
#if !COREAI_NO_LLM && !UNITY_WEBGL
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// G6 — free-form visual build. This group gives the model creative freedom and grades leniently,
    /// while preserving the model-authored positions for the report hero screenshot.
    /// </summary>
    internal static class GameFreeBuildScenariosG6
    {
        public static GameBenchmarkScenario[] All() => new GameBenchmarkScenario[]
        {
            new FreeBuildScene()
        };

        private abstract class G6Scenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G6";

            // Build-appropriate system prompt: the base GameMaster prompt ("smallest correct set of tool
            // calls") is wrong for a free build, where the model must place every object itself.
            public override string SystemPrompt =>
                "You are a 3D scene builder. Use the world_command tool to place each object yourself, " +
                "one spawn tool call per object, with explicit coordinates and meter-scale dimensions. " +
                "One Unity unit is one meter. Use scaleX/scaleY/scaleZ for non-uniform parts such as " +
                "walls, floors, roads, slabs, bridges and towers; do not rely only on default 1m objects. " +
                "Keep building a rich, structured scene " +
                "until it is complete — do not stop early and do not ask questions. " +
                "Vary positions, sizes and angles.";

            public override int Difficulty => 5;
            public override bool CaptureScene => true;
            public override bool FreeBuildLayout => true;
            public override bool Repeatable => false; // visual hero, never repeated/averaged
            // Tool-call cap for the visual build. Default 1000 — effectively "build as much as you want" for
            // any real model, while still a HARD safety valve so a weak model that spams identical spawns
            // can never loop forever and hang the run. When the cap is hit
            // the client returns FinishReason.Stop, the scenario completes normally, and the hero screenshot
            // is still captured. Override with COREAI_BENCHMARK_FREEBUILD_ROUNDTRIPS (e.g. set 200 for a quick
            // pass, or higher for an enormous scene).
            public const int DefaultFreeBuildRoundtrips = 1000;

            public static int FreeBuildRoundtrips()
            {
                string raw = System.Environment.GetEnvironmentVariable("COREAI_BENCHMARK_FREEBUILD_ROUNDTRIPS");
                if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int v) && v >= 1)
                {
                    return v;
                }

                return DefaultFreeBuildRoundtrips;
            }

            public override int? MaxToolCallRoundtripsOverride => FreeBuildRoundtrips();
            public override int TokenBudget => 6000;
            public override int MaxOutputTokens => 4800; // headroom per turn for many spawn tool-calls
            public override double TimeBudgetMs => 45000;
            // Per-scenario wall-clock for the visual build. This is also the deadline the model is told about
            // and counted down to after each spawn, so it can pace itself. 600s (10 min) matches the default
            // suite budget; override via COREAI_BENCHMARK_TIMEOUT. The roundtrip cap stays the hard backstop.
            public override float TimeoutSeconds => 600f;

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.WorldTool())
                    .WithStreaming(false)
                    .WithMaxOutputTokens(MaxOutputTokens)
                    // High but finite cap (default 1000) — the model builds freely, yet can never loop
                    // forever. The per-scenario override (AiTaskRequest) is the channel that actually reaches
                    // the client; this agent-level value is a redundant safety net.
                    .WithMaxToolCallRoundtrips(FreeBuildRoundtrips())
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
            }

            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run)
            {
                g.Add("used_tools", "issued at least one tool call", 10, run.ToolCalls >= 1,
                    mandatory: true,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.Add("clean_tools", "no failed tool calls or invalid world commands", 10,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0,
                    mandatory: true,
                    dimension: BenchmarkDimension.ToolCorrectness,
                    detail: $"{run.FailedToolCalls} failed, {env.World.InvalidCommandCount} invalid");
            }
        }

        private sealed class FreeBuildScene : G6Scenario
        {
            public override string Id => "g6_free_build";
            public override string Name => FreeBuildSubject() != null
                ? $"Free build: {FreeBuildSubject()}"
                : "Free build (visual)";

            public override string WhatItChecks =>
                "Free-form build: the model designs and places a whole scene from scratch — judged loosely on scale and variety, shown as the report hero image.";

            // The visual free-build is overridable with no code change:
            //   COREAI_BENCHMARK_FREEBUILD_PROMPT      — a full custom prompt, used verbatim
            //   COREAI_BENCHMARK_FREEBUILD_SUBJECT     — just the subject (e.g. "a futuristic city"); a generic
            //                                            spatial-build scaffold is generated around it
            //   COREAI_BENCHMARK_FREEBUILD_ROUNDTRIPS  — tool-call cap (default 1000); the test ends and the
            //                                            hero screenshot is taken when the model hits it
            // With none set, the default is the detailed castle prompt below with a 1000 tool-call cap.
            private static string Env(string key)
            {
                string v = System.Environment.GetEnvironmentVariable(key);
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }

            private static string FreeBuildSubject() => Env("COREAI_BENCHMARK_FREEBUILD_SUBJECT");

            public override string Goal
            {
                get
                {
                    string full = Env("COREAI_BENCHMARK_FREEBUILD_PROMPT");
                    string baseGoal = full
                        ?? (FreeBuildSubject() is string subject ? GenericGoal(subject) : CastleGoal);

                    // Tell the model it is on a clock and that every spawn result reports the time left, so it
                    // can pace itself and stop cleanly before the deadline rather than being cut off.
                    return baseGoal +
                        "\n\nYou are on a time budget. After every spawn, the tool result tells you how many " +
                        "seconds remain. Pace yourself: keep building steadily, and when the time is nearly up, " +
                        "stop spawning and finish — a complete smaller scene beats a half-built large one.";
                }
            }

            private static string GenericGoal(string subject) =>
                $"Build the most impressive {subject} you can. This is a showcase of your 3D spatial reasoning: " +
                "the more complete, structured and detailed, the better you score. Use the world_command tool only, " +
                "action='spawn', a DISTINCT targetName for every object, prefabKey for what to create, and explicit " +
                "x,y,z coordinates within the -9..9 range so the whole scene fits in one screenshot. One Unity unit " +
                "is one meter (y is height, larger y = higher; ground at y=0).\n\n" +
                "Pick the primitive that best fits each part via prefabKey — one of: cube, sphere, cylinder, capsule, " +
                "quad. For the ground, use a wide, flat, low cube with scaleX/scaleY/scaleZ (for example scaleX=16, " +
                "scaleY=0.2, scaleZ=16 at y=0) or a quad.\n\n" +
                $"Aim for AT LEAST 24 objects — ideally 30+, arranged so the result clearly reads as {subject}. " +
                "Keep every targetName distinct. Do not stop early: keep emitting spawn calls until it is full and " +
                "detailed — quantity and structure come first.\n\n" +
                "Give it natural variety — varied sizes and angles — so it does not read as a grid of identical " +
                "and rotations fx/fy/fz for angled pieces, so it does not read as a grid of identical 1m cubes. " +
                "Use scaleX/scaleY/scaleZ for long, tall, wide or thin parts.";

            private const string CastleGoal =
                "Build the most impressive castle you can. This is a showcase of your 3D spatial reasoning: the more " +
                "complete, structured and detailed the castle, the better you score. Use the world_command tool only, " +
                "action='spawn', a DISTINCT targetName for every object, prefabKey for what to create, and explicit " +
                "x,y,z coordinates within the -9..9 range so the whole castle fits in one screenshot. One Unity unit " +
                "is one meter (y is height, larger y = higher; the ground is at y=0).\n\n" +
                "Pick the primitive that best fits each part via prefabKey — one of: cube, sphere, cylinder, capsule, " +
                "quad. For example cylinders for round towers and flag poles, cubes for walls/keep/battlements, " +
                "spheres for domes/treetops, and a wide flat low cube (or a quad) for the ground.\n\n" +
                "A castle MUST have, at minimum: four corner towers, walls connecting them into a closed perimeter, a " +
                "gate gap at the front, and a central keep. Then add grandeur: battlements along the walls, flags on " +
                "top of the towers, roofs, a bridge, a moat ring, trees and torches outside.\n\n" +
                "If you are unsure how to lay it out, follow this proven skeleton and then EXTEND it with more detail:\n" +
                "- Ground: prefabKey='cube' at (0,0,0), scaleX=18, scaleY=0.2, scaleZ=18.\n" +
                "- Four corner towers: prefabKey='cylinder' at (-6,1.5,-6), (6,1.5,-6), (-6,1.5,6), (6,1.5,6), scaleX=1.4, scaleY=3, scaleZ=1.4.\n" +
                "- Walls: cubes connecting towers. A wall piece is about 2 meters long and thin: scaleX=2, scaleY=1.2, scaleZ=0.35 for east-west walls, or scaleX=0.35, scaleY=1.2, scaleZ=2 for north-south walls. " +
                "Leave the front edge z=6 open in the middle for the gate.\n" +
                "- Keep: several cubes near (0,1,0), at least 3 meters wide/tall using scaleX/scaleY/scaleZ.\n" +
                "- Flags: prefabKey='cylinder' on top of each tower at y=4, with thin scaleX/scaleZ and taller scaleY.\n" +
                "- Battlements: small cubes at y=3 along the wall tops; torches (cylinders) flanking the gate.\n\n" +
                "Aim for AT LEAST 24 objects — ideally 30+. Place walls so the towers actually connect into a " +
                "perimeter and keep every targetName distinct. Do not stop early: keep emitting spawn calls until the " +
                "castle is full and detailed — quantity and structure come first.\n\n" +
                "Give it natural variety — varied tower heights, differently sized pieces, angled roofs — so it " +
                "does not read as a grid of identical cubes. Use scaleX, scaleY, scaleZ and rotations fx/fy/fz " +
                "directly in spawn calls; do not build everything from default 1m cubes.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                int spawns = env.World.Count("spawn");
                List<RecordedWorldCommand> spawnCommands = SpawnCommands(env);
                int distinctNames = CountDistinctNames(spawnCommands);
                int boundViolations = CountBoundsViolations(spawnCommands);
                int cornerTowerQuadrants = CountCornerTowerQuadrants(spawnCommands);
                int wallSides = CountWallSides(spawnCommands);
                bool gateGap = HasGateGap(spawnCommands);
                bool centralKeep = HasCentralKeep(spawnCommands);
                int transformed = CountTransformedPieces(spawnCommands);
                int nonUniformScaled = CountNonUniformScaledPieces(spawnCommands);
                int extras = CountCastleExtras(spawnCommands);
                int prefabKinds = CountPrefabKinds(spawnCommands);
                int occupiedCells = CountOccupiedCells(spawnCommands);
                int namedDetailGroups = CountNamedDetailGroups(spawnCommands);

                bool genericFreeBuild = IsCustomFreeBuild();
                g.Add("substantial_scene", genericFreeBuild ? "built at least 18 scene pieces" : "built at least 24 castle pieces",
                    genericFreeBuild ? 20 : 10, spawns >= (genericFreeBuild ? 18 : 24), mandatory: true,
                    dimension: BenchmarkDimension.TaskCompletion, detail: $"{spawns} spawn commands");
                g.Add("distinct_named_pieces", "used distinct target names", genericFreeBuild ? 15 : 10,
                    distinctNames >= (genericFreeBuild ? 14 : 20),
                    dimension: BenchmarkDimension.ToolCorrectness, detail: $"{distinctNames} distinct names");
                g.Add("within_build_volume", "kept pieces inside the -9..9 build volume", genericFreeBuild ? 15 : 10,
                    boundViolations == 0, mandatory: true,
                    dimension: BenchmarkDimension.InstructionAdherence,
                    detail: boundViolations == 0 ? "all checked spawns in bounds" : $"{boundViolations} out of bounds");
                if (genericFreeBuild)
                {
                    g.Add("prefab_variety", "used varied primitive/prefab kinds", 15,
                        prefabKinds >= 3,
                        dimension: BenchmarkDimension.TaskCompletion,
                        detail: $"{prefabKinds} prefab kinds");
                    g.Add("position_variety", "spread pieces across the scene instead of one stack", 15,
                        occupiedCells >= 10,
                        dimension: BenchmarkDimension.TaskCompletion,
                        detail: $"{occupiedCells} occupied cells");
                }

                if (!genericFreeBuild)
                {
                    g.Add("corner_towers", "placed four recognizable corner towers", 15,
                        cornerTowerQuadrants >= 4, mandatory: true,
                        dimension: BenchmarkDimension.TaskCompletion,
                        detail: $"{cornerTowerQuadrants}/4 tower quadrants");
                    g.Add("connected_perimeter", "built wall runs on all four sides", 15,
                        wallSides >= 4, mandatory: true,
                        dimension: BenchmarkDimension.TaskCompletion,
                        detail: $"{wallSides}/4 wall sides");
                    g.Add("front_gate_gap", "left a front gate gap between side wall runs", 8, gateGap,
                        dimension: BenchmarkDimension.TaskCompletion);
                    g.Add("central_keep", "built a central keep near the castle middle", 10, centralKeep,
                        dimension: BenchmarkDimension.TaskCompletion);
                }

                g.Add("transform_variety", "used explicit scale or rotation for varied sizes/angles",
                    genericFreeBuild ? 15 : 12, transformed >= (genericFreeBuild ? 4 : 6),
                    dimension: BenchmarkDimension.InstructionAdherence,
                    detail: $"{transformed} transformed spawns");
                g.Add("non_uniform_scale", "used scaleX/scaleY/scaleZ for meter-sized parts",
                    genericFreeBuild ? 10 : 12, nonUniformScaled >= (genericFreeBuild ? 3 : 6),
                    mandatory: !genericFreeBuild,
                    dimension: BenchmarkDimension.InstructionAdherence,
                    detail: $"{nonUniformScaled} non-uniform scaled spawns");
                g.Add("detail_groups", genericFreeBuild
                        ? "added named detail groups beyond the core layout"
                        : "added flags, battlements, moat, bridge, torches, trees, or roofs",
                    genericFreeBuild ? 5 : 10,
                    genericFreeBuild ? namedDetailGroups >= 3 : extras >= 3,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: genericFreeBuild
                        ? $"{namedDetailGroups} named detail groups"
                        : $"{extras} extra-detail groups");

                return g;
            }

            private static List<RecordedWorldCommand> SpawnCommands(BenchmarkEnvironment env)
            {
                List<RecordedWorldCommand> result = new();
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (IsAction(c, "spawn"))
                    {
                        result.Add(c);
                    }
                }

                return result;
            }

            private static int CountDistinctNames(List<RecordedWorldCommand> commands)
            {
                HashSet<string> names = new(System.StringComparer.OrdinalIgnoreCase);
                foreach (RecordedWorldCommand c in commands)
                {
                    if (!string.IsNullOrWhiteSpace(c.TargetName))
                    {
                        names.Add(c.TargetName.Trim());
                    }
                }

                return names.Count;
            }

            private static int CountBoundsViolations(List<RecordedWorldCommand> commands)
            {
                int violations = 0;
                foreach (RecordedWorldCommand c in commands)
                {
                    if (c.X < -9f || c.X > 9f || c.Z < -9f || c.Z > 9f || c.Y < -1f || c.Y > 9f)
                    {
                        violations++;
                    }
                }

                return violations;
            }

            private static int CountCornerTowerQuadrants(List<RecordedWorldCommand> commands)
            {
                bool backLeft = false;
                bool backRight = false;
                bool frontLeft = false;
                bool frontRight = false;
                foreach (RecordedWorldCommand c in commands)
                {
                    if (!IsTowerLike(c) || System.Math.Abs(c.X) < 4.5f || System.Math.Abs(c.Z) < 4.5f)
                    {
                        continue;
                    }

                    if (c.X < 0f && c.Z < 0f) { backLeft = true; }
                    else if (c.X > 0f && c.Z < 0f) { backRight = true; }
                    else if (c.X < 0f && c.Z > 0f) { frontLeft = true; }
                    else if (c.X > 0f && c.Z > 0f) { frontRight = true; }
                }

                return (backLeft ? 1 : 0) + (backRight ? 1 : 0) + (frontLeft ? 1 : 0) + (frontRight ? 1 : 0);
            }

            private static int CountWallSides(List<RecordedWorldCommand> commands)
            {
                int back = 0;
                int front = 0;
                int left = 0;
                int right = 0;
                foreach (RecordedWorldCommand c in commands)
                {
                    if (!IsWallLike(c))
                    {
                        continue;
                    }

                    if (c.Z <= -4.8f && System.Math.Abs(c.X) <= 6.8f) { back++; }
                    if (c.Z >= 4.8f && System.Math.Abs(c.X) <= 6.8f) { front++; }
                    if (c.X <= -4.8f && System.Math.Abs(c.Z) <= 6.8f) { left++; }
                    if (c.X >= 4.8f && System.Math.Abs(c.Z) <= 6.8f) { right++; }
                }

                int sides = 0;
                if (back >= 3) { sides++; }
                if (front >= 2) { sides++; }
                if (left >= 3) { sides++; }
                if (right >= 3) { sides++; }
                return sides;
            }

            private static bool HasGateGap(List<RecordedWorldCommand> commands)
            {
                bool leftFront = false;
                bool rightFront = false;
                int blockedCenter = 0;
                foreach (RecordedWorldCommand c in commands)
                {
                    if (!IsWallLike(c) || c.Z < 4.8f)
                    {
                        continue;
                    }

                    if (c.X <= -1.5f) { leftFront = true; }
                    else if (c.X >= 1.5f) { rightFront = true; }
                    else { blockedCenter++; }
                }

                return leftFront && rightFront && blockedCenter == 0;
            }

            private static bool HasCentralKeep(List<RecordedWorldCommand> commands)
            {
                int centralPieces = 0;
                foreach (RecordedWorldCommand c in commands)
                {
                    bool namedKeep = Contains(c.TargetName, "keep") || Contains(c.TargetName, "tower_main");
                    bool central = System.Math.Abs(c.X) <= 2.5f && System.Math.Abs(c.Z) <= 2.5f;
                    bool substantial = c.Y >= 1f || c.FloatValue >= 1.2f || c.ScaleY >= 1.2f;
                    if (namedKeep || (central && substantial && IsBlockLike(c)))
                    {
                        centralPieces++;
                    }
                }

                return centralPieces >= 2;
            }

            private static int CountTransformedPieces(List<RecordedWorldCommand> commands)
            {
                int count = 0;
                foreach (RecordedWorldCommand c in commands)
                {
                    bool scaled = (c.FloatValue > 0f && System.Math.Abs(c.FloatValue - 1f) > 0.05f) ||
                                  c.ScaleX > 0f || c.ScaleY > 0f || c.ScaleZ > 0f;
                    bool rotated = c.Fx != 0f || c.Fy != 0f || c.Fz != 0f;
                    if (scaled || rotated)
                    {
                        count++;
                    }
                }

                return count;
            }

            private static int CountNonUniformScaledPieces(List<RecordedWorldCommand> commands)
            {
                int count = 0;
                foreach (RecordedWorldCommand c in commands)
                {
                    if (c.ScaleX > 0f || c.ScaleY > 0f || c.ScaleZ > 0f)
                    {
                        count++;
                    }
                }

                return count;
            }

            private static int CountCastleExtras(List<RecordedWorldCommand> commands)
            {
                HashSet<string> groups = new(System.StringComparer.OrdinalIgnoreCase);
                foreach (RecordedWorldCommand c in commands)
                {
                    string name = c.TargetName ?? "";
                    if (Contains(name, "flag")) { groups.Add("flag"); }
                    if (Contains(name, "battlement") || Contains(name, "crenel")) { groups.Add("battlement"); }
                    if (Contains(name, "moat")) { groups.Add("moat"); }
                    if (Contains(name, "bridge")) { groups.Add("bridge"); }
                    if (Contains(name, "torch")) { groups.Add("torch"); }
                    if (Contains(name, "tree")) { groups.Add("tree"); }
                    if (Contains(name, "roof")) { groups.Add("roof"); }
                }

                return groups.Count;
            }

            private static int CountPrefabKinds(List<RecordedWorldCommand> commands)
            {
                HashSet<string> kinds = new(System.StringComparer.OrdinalIgnoreCase);
                foreach (RecordedWorldCommand c in commands)
                {
                    if (!string.IsNullOrWhiteSpace(c.PrefabKeyOrName))
                    {
                        kinds.Add(c.PrefabKeyOrName.Trim());
                    }
                }

                return kinds.Count;
            }

            private static int CountOccupiedCells(List<RecordedWorldCommand> commands)
            {
                HashSet<string> cells = new(System.StringComparer.Ordinal);
                foreach (RecordedWorldCommand c in commands)
                {
                    int x = (int)System.Math.Round(c.X);
                    int z = (int)System.Math.Round(c.Z);
                    cells.Add($"{x}:{z}");
                }

                return cells.Count;
            }

            private static int CountNamedDetailGroups(List<RecordedWorldCommand> commands)
            {
                HashSet<string> groups = new(System.StringComparer.OrdinalIgnoreCase);
                foreach (RecordedWorldCommand c in commands)
                {
                    string name = c.TargetName ?? "";
                    foreach (string token in new[] { "detail", "tree", "light", "lamp", "door", "roof", "road", "bridge", "flag", "window", "prop" })
                    {
                        if (Contains(name, token))
                        {
                            groups.Add(token);
                        }
                    }
                }

                return groups.Count;
            }

            private static bool IsCustomFreeBuild()
                => Env("COREAI_BENCHMARK_FREEBUILD_PROMPT") != null ||
                   Env("COREAI_BENCHMARK_FREEBUILD_SUBJECT") != null;

            private static bool IsAction(RecordedWorldCommand c, string action)
                => string.Equals(c.Action, action, System.StringComparison.OrdinalIgnoreCase);

            private static bool IsTowerLike(RecordedWorldCommand c)
                => Contains(c.TargetName, "tower") || Contains(c.PrefabKeyOrName, "cylinder") ||
                   Contains(c.PrefabKeyOrName, "capsule");

            private static bool IsWallLike(RecordedWorldCommand c)
                => Contains(c.TargetName, "wall") || Contains(c.TargetName, "battlement") ||
                   Contains(c.TargetName, "gate") || Contains(c.PrefabKeyOrName, "cube");

            private static bool IsBlockLike(RecordedWorldCommand c)
                => Contains(c.PrefabKeyOrName, "cube") || Contains(c.TargetName, "keep") ||
                   Contains(c.TargetName, "wall");

            private static bool Contains(string value, string pattern)
                => !string.IsNullOrEmpty(value) &&
                   value.IndexOf(pattern, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
#endif
#endif
