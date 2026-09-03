#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
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
                "You are a 3D scene builder working in the Roblox API. Use the execute_lua tool and build " +
                "with Instance.new('Part'): set Name, Size (Vector3), CFrame, Material, Color, Shape, " +
                "Anchored = true and Parent = workspace. One unit is one Roblox stud. Send several parts " +
                "per execute_lua call — a whole section at a time — and keep calling until the scene is " +
                "complete; do not stop early and do not ask questions. Pick the Enum.Material each surface " +
                "would really be made of and the Enum.PartType shape that fits it; a scene of grey blocks " +
                "reads as unfinished. Vary positions, sizes and angles.";

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
                    .WithTool(env.RbxWorld.Tool)
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
                // WHY: require at least one tool call so a DO-NOTHING run cannot bank these points vacuously
                // (zero calls trivially has zero failures/invalid commands). "Clean" must mean "acted cleanly".
                g.Add("clean_tools", "no failed tool calls", 10,
                    run.ToolCalls >= 1 && run.FailedToolCalls == 0,
                    true,
                    dimension: BenchmarkDimension.ToolCorrectness,
                    detail: $"{run.ToolCalls} calls, {run.FailedToolCalls} failed");
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
                    $"Build the most impressive {subject} you can with the Roblox API. This is a showcase of " +
                    "your 3D spatial reasoning AND of material choice: the more complete, structured and " +
                    "believably surfaced it is, the better you score.\n\n" + RbxHowTo +
                    $"Aim for AT LEAST 40 parts — ideally 60+ — arranged so the result clearly reads as {subject}. " +
                    "Keep every Name distinct and prefixed with 'Castle' so the grader can find your parts " +
                    "(for example CastleGround, CastleWallNorth, CastleTowerNE).";
            }

            /// <summary>
            /// The Roblox-API contract every G6 goal shares: how to spawn a Part, the shapes that exist,
            /// the materials worth using and the build volume the hero camera frames.
            /// </summary>
            private const string RbxHowTo =
                "Use the execute_lua tool only. Build with the Roblox API:\n" +
                "  local p = Instance.new('Part')\n" +
                "  p.Name = 'CastleWallNorth'\n" +
                "  p.Size = Vector3.new(64, 11, 3)\n" +
                "  p.CFrame = CFrame.new(0, 5.5, 32) * CFrame.Angles(0, 0, 0)\n" +
                "  p.Material = Enum.Material.Cobblestone\n" +
                "  p.Color = Color3.fromRGB(146, 141, 132)\n" +
                "  p.Shape = Enum.PartType.Block\n" +
                "  p.Anchored = true\n" +
                "  p.Parent = workspace\n" +
                "Emit MANY parts per execute_lua call (a whole section at a time) — a local helper " +
                "function that takes name/size/cframe/material/color/shape keeps the code short. Lua 5.2 " +
                "syntax only: no '+=', no 'continue', no type annotations.\n\n" +
                "SHAPES — use ALL FIVE Enum.PartType values, each where it belongs: Block for walls, " +
                "floors and slabs; Cylinder for round towers, pillars, wells, chimneys and bars (a Cylinder " +
                "runs along X, so a vertical drum needs CFrame.Angles(0, 0, math.rad(90))); Wedge for " +
                "gabled roofs, ramps and stairs; CornerWedge for cone roof quadrants and corner braces; " +
                "Ball for domes, finials and lamps.\n\n" +
                "MATERIALS — choose what each surface would really be. Available Enum.Material values " +
                "include Cobblestone, Brick, Slate, Limestone, Sandstone, Granite, Basalt, Rock, Concrete, " +
                "Marble, Plaster, Pavement, Pebble, CeramicTiles, ClayRoofTiles, RoofShingles, Wood, " +
                "WoodPlanks, Metal, CorrodedMetal, DiamondPlate, Foil, Glass, Neon, Fabric, Carpet, " +
                "Leather, Cardboard, Rubber, Grass, LeafyGrass, Ground, Mud, Sand, Snow, Ice, CrackedLava, " +
                "Asphalt, Plastic, SmoothPlastic, ForceField. Use at least 12 DIFFERENT ones. Set " +
                "p.Color as well: the colour tints the material's texture, so keep tints natural " +
                "(Color3.fromRGB) rather than fully saturated.\n\n" +
                "VOLUME — keep every part within x and z of -64..64 studs and y of 0..96 studs so the whole " +
                "scene fits in one screenshot. Ground sits at y = 0.\n\n";

            private const string CastleGoal =
                "Build the most impressive castle you can with the Roblox API. This is a showcase of your 3D " +
                "spatial reasoning AND of material choice: the more complete, structured and believably " +
                "surfaced the castle, the better you score.\n\n" + RbxHowTo +
                "It should clearly read as a castle, but HOW you compose it is up to you — do not just place " +
                "a ring of four towers and stop. Think about what makes a castle memorable and CHOOSE what to " +
                "include, for example: a lived-in courtyard (a well, market stalls, crates, barrels, benches, " +
                "a campfire), buildings of different heights and rooflines, a gatehouse with an arch and a " +
                "portcullis, a drawbridge over a moat, a road leading up to it, and a surrounding world that " +
                "continues past the walls — outbuildings, gardens, tents, trees, rocks, fences. The area " +
                "BEHIND and AROUND the castle should not be empty. Uneven, asymmetric, hand-placed layouts " +
                "read as more real than a perfect grid.\n\n" +
                "Depth and detail are what score here: aim for AT LEAST 40 parts, ideally 60+, with at least " +
                "12 different Enum.Material values and all five Enum.PartType shapes. Name every part " +
                "distinctly and start every Name with 'Castle' (CastleGround, CastleTowerNE, CastleGate, ...) " +
                "so the grader can find them.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                // WHY: the scene is read back through the same Rbx surface the model built with, so the
                // grade reflects what actually materialised — not what the tool calls claimed.
                RbxBenchmarkWorld.Snapshot scene = env.HasRbxWorld
                    ? env.RbxWorld.Measure()
                    : default;
                bool genericFreeBuild = IsCustomFreeBuild();

                g.Add("substantial_scene",
                    genericFreeBuild ? "built at least 40 scene parts" : "built at least 40 castle parts",
                    genericFreeBuild ? 20 : 16, scene.Parts >= 40, true,
                    dimension: BenchmarkDimension.TaskCompletion, detail: $"{scene.Parts} parts in the world");
                g.Add("distinct_named_pieces", "gave every part a distinct name", 10,
                    scene.DistinctNames >= 20,
                    dimension: BenchmarkDimension.ToolCorrectness, detail: $"{scene.DistinctNames} distinct names");
                g.Add("within_build_volume", "kept parts inside the build volume", 10,
                    scene.Parts >= 1 && scene.OutOfBounds == 0, true,
                    dimension: BenchmarkDimension.InstructionAdherence,
                    detail: scene.OutOfBounds == 0 ? "all parts in bounds" : $"{scene.OutOfBounds} out of bounds");

                // The two checkpoints this scenario exists for: real materials and the full shape set.
                g.Add("material_variety", "surfaced the scene with at least 12 different Enum.Material values",
                    20, scene.Materials.Count >= 12,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: $"{scene.Materials.Count} materials: {string.Join(",", scene.Materials)}");
                g.Add("material_breadth", "went beyond a couple of stone-and-wood defaults", 8,
                    scene.Materials.Count >= 20,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: $"{scene.Materials.Count} materials");
                g.Add("shape_variety", "used all five Enum.PartType shapes", 20,
                    scene.Shapes.Count >= 5,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: $"{scene.Shapes.Count} shapes: {string.Join(",", scene.Shapes)}");
                g.Add("shape_beyond_blocks", "did not build out of blocks alone", 8,
                    scene.Shapes.Count >= 3,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: $"{scene.Shapes.Count} shapes");
                g.Add("scene_depth", "kept building past a bare wall ring", 8,
                    scene.Parts >= 60,
                    dimension: BenchmarkDimension.TaskCompletion, detail: $"{scene.Parts} parts");

                return g;
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
                    .WithTool(env.RbxWorld.Tool);

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
