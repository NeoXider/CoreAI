#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
#if !COREAI_NO_LLM && !UNITY_WEBGL
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
                "one tool call per object, with explicit coordinates. Keep building a rich, structured scene " +
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
                g.Add("used_tools", "issued at least one tool call", 0, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.Add("clean_tools", "no failed tool calls or invalid world commands", 0,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0,
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
                "action='spawn', a DISTINCT targetName for every object, and explicit x,y,z coordinates within the " +
                "-9..9 range so the whole scene fits in one screenshot (y is height, larger y = higher; ground at y=0).\n\n" +
                "Pick the primitive that best fits each part via prefabKey — one of: cube, sphere, cylinder, capsule, " +
                "quad. For the ground, use a wide, flat, low cube (a cube scaled large in X/Z, e.g. scale a cube at " +
                "y=0) or a quad.\n\n" +
                $"Aim for AT LEAST 24 objects — ideally 30+, arranged so the result clearly reads as {subject}. " +
                "Keep every targetName distinct. Do not stop early: keep emitting spawn calls until it is full and " +
                "detailed — quantity and structure come first.\n\n" +
                "Give it natural variety — varied sizes and angles — so it does not read as a grid of identical " +
                "cubes. Use whatever the tool offers to achieve that.";

            private const string CastleGoal =
                "Build the most impressive castle you can. This is a showcase of your 3D spatial reasoning: the more " +
                "complete, structured and detailed the castle, the better you score. Use the world_command tool only, " +
                "action='spawn', a DISTINCT targetName for every object, and explicit x,y,z coordinates within the " +
                "-9..9 range so the whole castle fits in one screenshot (y is height, larger y = higher; the ground is " +
                "at y=0).\n\n" +
                "Pick the primitive that best fits each part via prefabKey — one of: cube, sphere, cylinder, capsule, " +
                "quad. For example cylinders for round towers and flag poles, cubes for walls/keep/battlements, " +
                "spheres for domes/treetops, and a wide flat low cube (or a quad) for the ground.\n\n" +
                "A castle MUST have, at minimum: four corner towers, walls connecting them into a closed perimeter, a " +
                "gate gap at the front, and a central keep. Then add grandeur: battlements along the walls, flags on " +
                "top of the towers, roofs, a bridge, a moat ring, trees and torches outside.\n\n" +
                "If you are unsure how to lay it out, follow this proven skeleton and then EXTEND it with more detail:\n" +
                "- Ground: prefabKey='cube' at (0,0,0) scaled large in X/Z (a wide flat low slab), or prefabKey='quad'.\n" +
                "- Four corner towers: prefabKey='cylinder' at (-6,1.5,-6), (6,1.5,-6), (-6,1.5,6), (6,1.5,6).\n" +
                "- Walls (cubes) spaced every ~2 units along each of the four edges connecting the towers, e.g. the " +
                "back wall at z=-6 with x = -4,-2,0,2,4 (leave the front edge z=6 open in the middle for the gate).\n" +
                "- Keep: a few stacked cubes near (0,1,0).\n" +
                "- Flags: prefabKey='cylinder' on top of each tower at y=4.\n" +
                "- Battlements: small cubes at y=3 along the wall tops; torches (cylinders) flanking the gate.\n\n" +
                "Aim for AT LEAST 24 objects — ideally 30+. Place walls so the towers actually connect into a " +
                "perimeter and keep every targetName distinct. Do not stop early: keep emitting spawn calls until the " +
                "castle is full and detailed — quantity and structure come first.\n\n" +
                "Give it natural variety — varied tower heights, differently sized pieces, angled roofs — so it " +
                "does not read as a grid of identical cubes. Use whatever the tool offers to achieve that.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                int spawns = env.World.Count("spawn");
                g.Add("substantial_scene", "built a substantial scene", 60, spawns >= 12, mandatory: true,
                    dimension: BenchmarkDimension.TaskCompletion, detail: $"{spawns} spawn commands");
                g.Add("rich_variety_scale", "rich variety/scale", 40, spawns >= 20,
                    dimension: BenchmarkDimension.TaskCompletion, detail: $"{spawns} spawn commands");

                return g;
            }
        }
    }
}
#endif
#endif
