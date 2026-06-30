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
            public override int Difficulty => 5;
            public override bool CaptureScene => true;
            public override bool FreeBuildLayout => true;
            public override bool Repeatable => false; // visual hero, never repeated/averaged
            public override int TokenBudget => 6000;
            public override int MaxOutputTokens => 4800; // 24+ objects, each spawned AND coloured (set_color)
            public override double TimeBudgetMs => 45000;
            public override float TimeoutSeconds => 360f;

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.WorldTool())
                    .WithAllowDuplicateToolCalls(true)
                    .WithStreaming(false)
                    .WithMaxOutputTokens(MaxOutputTokens)
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

            // The visual free-build prompt is overridable, so you can ask for other things (a city, a
            // character, a spaceship...) with no code change:
            //   COREAI_BENCHMARK_FREEBUILD_PROMPT  — a full custom prompt, used verbatim
            //   COREAI_BENCHMARK_FREEBUILD_SUBJECT — just the subject (e.g. "a futuristic city"); a generic
            //                                        spatial-build scaffold is generated around it
            // With neither set, the default is the detailed castle prompt below.
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
                    if (full != null)
                    {
                        return full;
                    }

                    string subject = FreeBuildSubject();
                    return subject != null ? GenericGoal(subject) : CastleGoal;
                }
            }

            private static string GenericGoal(string subject) =>
                $"Build the most impressive {subject} you can. This is a showcase of your 3D spatial reasoning: " +
                "the more complete, structured and detailed, the better you score. Use the world_command tool only, " +
                "action='spawn', a DISTINCT targetName for every object, and explicit x,y,z coordinates within the " +
                "-9..9 range so the whole scene fits in one screenshot (y is height, larger y = higher; ground at y=0).\n\n" +
                "Pick the primitive that best fits each part via prefabKey — one of: cube, sphere, cylinder, capsule, " +
                "plane, quad (a plane makes a good ground).\n\n" +
                $"Aim for AT LEAST 24 objects — ideally 30+, arranged so the result clearly reads as {subject}. " +
                "Keep every targetName distinct. Do not stop early: keep emitting spawn calls until it is full and " +
                "detailed — quantity and structure come first.\n\n" +
                "For natural variety you may resize parts with action='set_scale' (a uniform 'scale') and turn pieces " +
                "with action='rotate' (fx/fy/fz degrees), only after you have spawned plenty of objects.";

            private const string CastleGoal =
                "Build the most impressive castle you can. This is a showcase of your 3D spatial reasoning: the more " +
                "complete, structured and detailed the castle, the better you score. Use the world_command tool only, " +
                "action='spawn', a DISTINCT targetName for every object, and explicit x,y,z coordinates within the " +
                "-9..9 range so the whole castle fits in one screenshot (y is height, larger y = higher; the ground is " +
                "at y=0).\n\n" +
                "Pick the primitive that best fits each part via prefabKey — one of: cube, sphere, cylinder, capsule, " +
                "plane, quad. For example cylinders for round towers and flag poles, cubes for walls/keep/battlements, " +
                "spheres for domes/treetops, a plane for the ground.\n\n" +
                "A castle MUST have, at minimum: four corner towers, walls connecting them into a closed perimeter, a " +
                "gate gap at the front, and a central keep. Then add grandeur: battlements along the walls, flags on " +
                "top of the towers, roofs, a bridge, a moat ring, trees and torches outside.\n\n" +
                "If you are unsure how to lay it out, follow this proven skeleton and then EXTEND it with more detail:\n" +
                "- Ground: prefabKey='plane' at (0,0,0).\n" +
                "- Four corner towers: prefabKey='cylinder' at (-6,1.5,-6), (6,1.5,-6), (-6,1.5,6), (6,1.5,6).\n" +
                "- Walls (cubes) spaced every ~2 units along each of the four edges connecting the towers, e.g. the " +
                "back wall at z=-6 with x = -4,-2,0,2,4 (leave the front edge z=6 open in the middle for the gate).\n" +
                "- Keep: a few stacked cubes near (0,1,0).\n" +
                "- Flags: prefabKey='cylinder' on top of each tower at y=4.\n" +
                "- Battlements: small cubes at y=3 along the wall tops; torches (cylinders) flanking the gate.\n\n" +
                "Aim for AT LEAST 24 objects — ideally 30+. Place walls so the towers actually connect into a " +
                "perimeter and keep every targetName distinct. Do not stop early: keep emitting spawn calls until the " +
                "castle is full and detailed — quantity and structure come first.\n\n" +
                "For natural variety you may resize parts with action='set_scale' (a uniform 'scale', e.g. taller " +
                "towers, a bigger keep) and turn pieces with action='rotate' (fx/fy/fz degrees). Only do this after " +
                "you have spawned plenty of objects — never let it reduce how many objects you build.";

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
