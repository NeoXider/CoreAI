#if !COREAI_NO_LUA
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
        /// <summary>
        /// G6 vision mode from COREAI_BENCHMARK_VISION_MODE: "off" (default), "image", or "both".
        /// </summary>
        private static string VisionMode()
        {
            string raw = System.Environment.GetEnvironmentVariable("COREAI_BENCHMARK_VISION_MODE");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "off";
            }

            raw = raw.Trim().ToLowerInvariant();
            return raw is "image" or "both" ? raw : "off";
        }

        public static GameBenchmarkScenario[] All()
        {
            string mode = VisionMode();
            List<GameBenchmarkScenario> scenarios = new();

            // Text-only build runs unless the mode is exclusively "image".
            if (mode != "image")
            {
                scenarios.Add(new FreeBuildScene());
            }

            // Image-feedback build runs for "image" and "both".
            if (mode is "image" or "both")
            {
                scenarios.Add(new FreeBuildSceneWithVision());
            }

            return scenarios.ToArray();
        }

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
                "Give parts appropriate colors with action='set_color' (targetName + stringValue as an " +
                "HTML color) — an all-grey scene reads as unfinished. " +
                "Keep building a rich, structured scene " +
                "until it is complete — do not stop early and do not ask questions. " +
                "Vary positions, sizes and angles.";

            public override int Difficulty => 5;
            public override bool CaptureScene => true;
            public override bool FreeBuildLayout => true;

            public override int? RepsOverride => 1; // visual hero, never repeated/averaged

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

            public override double TimeBudgetMs => 45000;

            // Per-scenario wall-clock for the visual build. This is also the deadline the model is told about
            // and counted down to after each spawn, so it can pace itself. 600s (10 min) is intentionally
            // G6-specific; the whole-suite soft budget is much larger. Override via COREAI_BENCHMARK_TIMEOUT.
            // The roundtrip cap stays the hard backstop.
            public override float TimeoutSeconds => 600f;

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.WorldTool())
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
                    true,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.Add("clean_tools", "no failed tool calls or invalid world commands", 10,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0,
                    true,
                    dimension: BenchmarkDimension.ToolCorrectness,
                    detail: $"{run.FailedToolCalls} failed, {env.World.InvalidCommandCount} invalid");
            }
        }

        private class FreeBuildScene : G6Scenario
        {
            public override string Id => "g6_free_build";

            public override string Name => FreeBuildSubject() != null
                ? $"Free build: {FreeBuildSubject()}"
                : "Free build (visual)";

            // Only a FULL prompt override is excluded — it replaces the goal verbatim with arbitrary
            // operator text the built-in checkpoints were never designed for. A subject-only override
            // still uses our own GenericGoal scaffold below (known structure, "at least 24 objects" etc.),
            // so it stays gradeable with the existing generic-free-build checkpoints.
            public override bool ExcludeFromScoring => HasFullCustomPrompt();

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

            private static string FreeBuildSubject()
            {
                return Env("COREAI_BENCHMARK_FREEBUILD_SUBJECT");
            }

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

            private static string GenericGoal(string subject)
            {
                return
                    $"Build the most impressive {subject} you can. This is a showcase of your 3D spatial reasoning: " +
                    "the more complete, structured and detailed, the better you score. Use the world_command tool only, " +
                    "action='spawn', a DISTINCT targetName for every object, prefabKey for what to create, and explicit " +
                    "x,y,z coordinates within the -9..9 range so the whole scene fits in one screenshot. One Unity unit " +
                    "is one meter (y is height, larger y = higher; ground at y=0).\n\n" +
                    "Pick the primitive that best fits each part via prefabKey — one of: cube, sphere, cylinder, capsule. " +
                    "For the ground, use a wide, flat, low cube with scaleX/scaleY/scaleZ (for example scaleX=16, " +
                    "scaleY=0.2, scaleZ=16 at y=0). These primitives are NOT all the same base size: " +
                    "cube/sphere are 1m unscaled, but cylinder and capsule are already 2m TALL unscaled at 1m " +
                    "diameter — for a tower/pillar/trunk of height H standing on the ground, use scaleY = H/2 (not H) " +
                    "and place its pivot at y = H/2.\n\n" +
                    $"Aim for AT LEAST 24 objects — ideally 30+, arranged so the result clearly reads as {subject}. " +
                    "Keep every targetName distinct. Do not stop early: keep emitting spawn calls until it is full and " +
                    "detailed — quantity and structure come first.\n\n" +
                    "Give it natural variety — varied sizes, angles and rotations fx/fy/fz for angled pieces — so it " +
                    "does not read as a grid of identical 1m cubes. Use scaleX/scaleY/scaleZ for long, tall, wide or " +
                    "thin parts.\n\n" +
                    "COLOR the scene: use action='set_color' with targetName and stringValue as an HTML color " +
                    "(e.g. '#9aa0a8') to tint each major group appropriately — ground, structures, details. " +
                    "An all-grey scene loses points; color at least the main groups.";
            }

            private const string CastleGoal =
                "Build the most impressive castle you can. This is a showcase of your 3D spatial reasoning: the more " +
                "complete, structured and detailed the castle, the better you score. Use the world_command tool only, " +
                "action='spawn', a DISTINCT targetName for every object, prefabKey for what to create, and explicit " +
                "x,y,z coordinates within the -9..9 range so the whole castle fits in one screenshot. One Unity unit " +
                "is one meter (y is height, larger y = higher; the ground is at y=0).\n\n" +
                "Pick the primitive that best fits each part via prefabKey — one of: cube, sphere, cylinder, capsule. " +
                "For example cylinders for round towers and flag poles, cubes for walls/keep/battlements, " +
                "spheres for domes/treetops, and a wide flat low cube for the ground. These primitives " +
                "are NOT all the same base size: cube/sphere are 1m unscaled, but cylinder and capsule are " +
                "already 2m TALL unscaled at 1m diameter — for a tower/pillar of height H standing on the ground, " +
                "use scaleY = H/2 (not H) and place its pivot at y = H/2.\n\n" +
                "It should clearly read as a castle, but HOW you compose it is up to you — do not just place a " +
                "ring of four towers and stop. Aim for a place that feels alive and interesting to look at. " +
                "Think about what makes a castle memorable and CHOOSE what to include, for example: a lived-in " +
                "courtyard (a well, market stalls, crates, barrels, a training dummy, benches, a campfire), " +
                "buildings of different heights and rooflines, a gatehouse and a road leading up to it, and a " +
                "surrounding world that continues past the walls — outbuildings, gardens, tents, trees, a pond " +
                "or moat, rocks, fences, a watchtower on a hill. The area BEHIND and AROUND the castle should " +
                "not be empty. Uneven, asymmetric, hand-placed layouts read as more real than a perfect grid.\n\n" +
                "Depth and detail are what score here, not a fixed part count. Prefer many small, varied, " +
                "purposeful pieces (props, decorations, level-of-detail) over a few big blocks. Give every " +
                "object a DISTINCT targetName, and keep spawning until the scene feels full and finished.\n\n" +
                "Sizing note for standing pieces: cylinder and capsule are already 2m tall unscaled at 1m " +
                "diameter, so for a tower/pillar of height H use scaleY = H/2 and pivot y = H/2. Use " +
                "scaleX/scaleY/scaleZ and rotations fx/fy/fz freely for long, tall, thin or angled parts so it " +
                "does not read as a grid of identical cubes.\n\n" +
                "COLOR the castle: use action='set_color' with targetName and stringValue as an HTML color to " +
                "tint each major group — e.g. grey stone '#9aa0a8' walls and towers, dark red '#8e3b2f' roofs " +
                "and flags, brown '#6b4a2f' gate and bridge, green '#3f7d3a' treetops, blue '#3b6ea5' moat " +
                "water, warm '#d8b36a' torch tips. An all-grey castle loses points; color at least the major " +
                "groups.";

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
                g.Add("substantial_scene",
                    genericFreeBuild ? "built at least 24 scene pieces" : "built at least 24 castle pieces",
                    genericFreeBuild ? 20 : 10, spawns >= 24, true,
                    dimension: BenchmarkDimension.TaskCompletion, detail: $"{spawns} spawn commands");
                g.Add("distinct_named_pieces", "used distinct target names", genericFreeBuild ? 15 : 10,
                    distinctNames >= (genericFreeBuild ? 20 : 20),
                    dimension: BenchmarkDimension.ToolCorrectness, detail: $"{distinctNames} distinct names");
                g.Add("within_build_volume", "kept pieces inside the -9..9 build volume", genericFreeBuild ? 15 : 10,
                    boundViolations == 0, true,
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
                    // "Reads as a castle" is rewarded through recognizable castle-ness, but NONE of these are
                    // mandatory or dictate an exact layout: a model that expresses the castle differently
                    // (a keep-and-courtyard, a hillside fort, an asymmetric stronghold) is not punished. We
                    // reward the SIGNALS of a castle, each worth a little, so richness/variety/detail below
                    // carry the score — not a fixed four-towers-and-walls recipe.
                    int castleSignals =
                        (cornerTowerQuadrants >= 2 ? 1 : 0) + // some towers, not necessarily 4 corners
                        (wallSides >= 2 ? 1 : 0) + // some enclosing walls, not a full perimeter
                        (gateGap ? 1 : 0) +
                        (centralKeep ? 1 : 0);
                    g.Add("reads_as_castle", "shows recognizable castle features (towers/walls/gate/keep)", 12,
                        castleSignals >= 2,
                        dimension: BenchmarkDimension.TaskCompletion,
                        detail:
                        $"{castleSignals}/4 castle signals (towers={cornerTowerQuadrants}, walls={wallSides}, gate={gateGap}, keep={centralKeep})");
                    g.Add("castle_depth", "went beyond a bare wall ring — richer, fuller castle", 16,
                        castleSignals >= 3 && extras >= 4,
                        dimension: BenchmarkDimension.TaskCompletion,
                        detail: $"{castleSignals}/4 signals + {extras} detail groups");
                }

                g.Add("transform_variety", "used explicit scale or rotation for varied sizes/angles",
                    genericFreeBuild ? 15 : 12, transformed >= (genericFreeBuild ? 4 : 6),
                    dimension: BenchmarkDimension.InstructionAdherence,
                    detail: $"{transformed} transformed spawns");
                g.Add("non_uniform_scale", "used scaleX/scaleY/scaleZ for meter-sized parts",
                    genericFreeBuild ? 10 : 12, nonUniformScaled >= (genericFreeBuild ? 3 : 6),
                    !genericFreeBuild,
                    dimension: BenchmarkDimension.InstructionAdherence,
                    detail: $"{nonUniformScaled} non-uniform scaled spawns");
                g.Add("detail_groups", genericFreeBuild
                        ? "added named detail groups beyond the core layout"
                        : "filled it with props/decor/surroundings (courtyard, outbuildings, life around it)",
                    genericFreeBuild ? 5 : 14,
                    genericFreeBuild ? namedDetailGroups >= 3 : namedDetailGroups >= 5,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: genericFreeBuild
                        ? $"{namedDetailGroups} named detail groups"
                        : $"{namedDetailGroups} named detail groups / {extras} castle extras");

                // Soft, non-mandatory: the prompt now explicitly asks for set_color tints on the major
                // groups (an all-grey scene reads as unfinished in the hero shot).
                int colorCommands = env.World.Count("set_color");
                g.Add("used_color", "tinted pieces with set_color", 6, colorCommands >= 4,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: $"{colorCommands} set_color commands");

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
                    (float hx, float hy, float hz) = HalfExtents(c);
                    if (c.X - hx < -9f || c.X + hx > 9f ||
                        c.Z - hz < -9f || c.Z + hz > 9f ||
                        c.Y - hy < -1f || c.Y + hy > 9f)
                    {
                        violations++;
                    }
                }

                return violations;
            }

            private static (float x, float y, float z) HalfExtents(RecordedWorldCommand c)
            {
                float uniform = c.FloatValue > 0f ? c.FloatValue : 1f;
                float sx = c.ScaleX > 0f ? c.ScaleX : uniform;
                float sy = c.ScaleY > 0f ? c.ScaleY : uniform;
                float sz = c.ScaleZ > 0f ? c.ScaleZ : uniform;

                if (Contains(c.PrefabKeyOrName, "plane"))
                {
                    return (5f * sx, 0.01f * sy, 5f * sz);
                }

                float heightMultiplier = Contains(c.PrefabKeyOrName, "cylinder") ||
                                         Contains(c.PrefabKeyOrName, "capsule")
                    ? 1f
                    : 0.5f;
                return (0.5f * sx, heightMultiplier * sy, 0.5f * sz);
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

                    if (c.X < 0f && c.Z < 0f)
                    {
                        backLeft = true;
                    }
                    else if (c.X > 0f && c.Z < 0f)
                    {
                        backRight = true;
                    }
                    else if (c.X < 0f && c.Z > 0f)
                    {
                        frontLeft = true;
                    }
                    else if (c.X > 0f && c.Z > 0f)
                    {
                        frontRight = true;
                    }
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

                    if (c.Z <= -4.8f && System.Math.Abs(c.X) <= 6.8f)
                    {
                        back++;
                    }

                    if (c.Z >= 4.8f && System.Math.Abs(c.X) <= 6.8f)
                    {
                        front++;
                    }

                    if (c.X <= -4.8f && System.Math.Abs(c.Z) <= 6.8f)
                    {
                        left++;
                    }

                    if (c.X >= 4.8f && System.Math.Abs(c.Z) <= 6.8f)
                    {
                        right++;
                    }
                }

                int sides = 0;
                if (back >= 3)
                {
                    sides++;
                }

                if (front >= 2)
                {
                    sides++;
                }

                if (left >= 3)
                {
                    sides++;
                }

                if (right >= 3)
                {
                    sides++;
                }

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

                    if (c.X <= -1.5f)
                    {
                        leftFront = true;
                    }
                    else if (c.X >= 1.5f)
                    {
                        rightFront = true;
                    }
                    else
                    {
                        blockedCenter++;
                    }
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
                    if (Contains(name, "flag"))
                    {
                        groups.Add("flag");
                    }

                    if (Contains(name, "battlement") || Contains(name, "crenel"))
                    {
                        groups.Add("battlement");
                    }

                    if (Contains(name, "moat"))
                    {
                        groups.Add("moat");
                    }

                    if (Contains(name, "bridge"))
                    {
                        groups.Add("bridge");
                    }

                    if (Contains(name, "torch"))
                    {
                        groups.Add("torch");
                    }

                    if (Contains(name, "tree"))
                    {
                        groups.Add("tree");
                    }

                    if (Contains(name, "roof"))
                    {
                        groups.Add("roof");
                    }
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
                    foreach (string token in new[]
                             {
                                 "detail", "tree", "light", "lamp", "door", "roof", "road", "bridge", "flag", "window",
                                 "prop"
                             })
                    {
                        if (Contains(name, token))
                        {
                            groups.Add(token);
                        }
                    }
                }

                return groups.Count;
            }

            private static bool HasFullCustomPrompt()
            {
                return Env("COREAI_BENCHMARK_FREEBUILD_PROMPT") != null;
            }

            private static bool IsCustomFreeBuild()
            {
                return Env("COREAI_BENCHMARK_FREEBUILD_PROMPT") != null ||
                       Env("COREAI_BENCHMARK_FREEBUILD_SUBJECT") != null;
            }

            private static bool IsAction(RecordedWorldCommand c, string action)
            {
                return string.Equals(c.Action, action, System.StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsTowerLike(RecordedWorldCommand c)
            {
                if (!Contains(c.TargetName, "tower") &&
                    !Contains(c.PrefabKeyOrName, "cylinder") &&
                    !Contains(c.PrefabKeyOrName, "capsule"))
                {
                    return false;
                }

                (float hx, float hy, float hz) = HalfExtents(c);
                float height = hy * 2f;
                float footprint = System.Math.Max(hx * 2f, hz * 2f);
                return height >= 2.5f && footprint >= 1f;
            }

            private static bool IsWallLike(RecordedWorldCommand c)
            {
                return Contains(c.TargetName, "wall") || Contains(c.TargetName, "battlement") ||
                       Contains(c.TargetName, "gate") || Contains(c.PrefabKeyOrName, "cube");
            }

            private static bool IsBlockLike(RecordedWorldCommand c)
            {
                return Contains(c.PrefabKeyOrName, "cube") || Contains(c.TargetName, "keep") ||
                       Contains(c.TargetName, "wall");
            }

            private static bool Contains(string value, string pattern)
            {
                return !string.IsNullOrEmpty(value) &&
                       value.IndexOf(pattern, System.StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        /// <summary>
        /// Image-feedback variant of the free-build: the model additionally gets the <c>camera</c> tool so it
        /// can SEE its own work-in-progress (camera_capture returns a rendered screenshot) and refine it —
        /// the "look at what you made and fix it" loop. Only meaningful for vision-capable models; a text-only
        /// model simply never calls the camera and scores like the plain build. Grading is inherited unchanged
        /// so the image and text runs are directly comparable. Enabled via COREAI_BENCHMARK_VISION_MODE
        /// = image | both.
        /// </summary>
        private sealed class FreeBuildSceneWithVision : FreeBuildScene
        {
            public override string Id => "g6_free_build_vision";

            public override string Name => "Free build (visual, image feedback)";

            public override string WhatItChecks =>
                "Same free-form build, but the model can capture and look at its own scene and refine it — " +
                "measures whether vision feedback improves the result.";

            // Deliberately IDENTICAL to the plain free-build prompt: the image-feedback variant must NOT
            // mention the camera or coach the model to "look and refine". The only difference between the two
            // runs is that this one is handed the camera_capture tool (in BuildAgent) — whether the model
            // discovers and uses vision to improve its build on its own is exactly what this scenario measures.
            // Telling it to use vision would bias the A/B (and, empirically, coaching made the result worse).
            public override string SystemPrompt => base.SystemPrompt;

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                AgentBuilder b = new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.WorldTool());

                // Add the vision tool only when the world executor can actually render (visual mode). A null
                // camera tool means this run degrades to a text-only build rather than erroring.
                Vision.CameraLlmTool cam = env.CameraTool(RoleId);
                if (cam != null)
                {
                    b.WithTool(cam);
                }

                return b
                    .WithMaxOutputTokens(MaxOutputTokens)
                    .WithMaxToolCallRoundtrips(FreeBuildRoundtrips())
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
            }
        }
    }
}
#endif
#endif
