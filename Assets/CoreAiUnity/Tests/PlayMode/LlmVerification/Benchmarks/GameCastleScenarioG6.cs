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
    internal static class GameCastleScenariosG6
    {
        public static GameBenchmarkScenario[] All() => new GameBenchmarkScenario[]
        {
            new CastleFreeBuild()
        };

        private abstract class G6Scenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G6";
            public override int Difficulty => 5;
            public override bool CaptureScene => true;
            public override bool FreeBuildLayout => true;
            public override bool Repeatable => false; // visual hero, never repeated/averaged
            public override int TokenBudget => 4000;
            public override int MaxOutputTokens => 3200; // ~24+ verbose spawn calls without truncation
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

        private sealed class CastleFreeBuild : G6Scenario
        {
            public override string Id => "g6_castle";
            public override string Name => "Castle (free build)";

            public override string WhatItChecks =>
                "Free-form build: the model designs and places a whole castle scene — judged loosely on scale and variety, shown as the report hero image.";

            public override string Goal =>
                "Build the most impressive castle you can — full creative freedom over its shape and details. " +
                "Use the world_command tool only, action='spawn', prefabKey='Cube', a DISTINCT targetName for every " +
                "object, and explicit x,y,z coordinates kept within the -9..9 range so the whole castle fits in one " +
                "screenshot (y is height, larger y = higher).\n\n" +
                "Make it read like a real castle and pack in as much detail as you can manage. Use names that hint at " +
                "what each object is so the scene is colourful and legible:\n" +
                "- tall corner Towers placed so the walls between them form a real enclosed perimeter;\n" +
                "- Walls connecting the towers, with a Gate or Door gap at the front;\n" +
                "- a central Keep (or Castle) block in the middle;\n" +
                "- Flags high on the towers (use a larger y);\n" +
                "- then add as many extras as you like for grandeur: battlements, a Bridge, a Moat/Water ring, " +
                "Trees and Torches outside, banners, courtyard props, stairs, or roofs.\n\n" +
                "Aim for AT LEAST 24 objects — the bigger, more detailed, and better-arranged, the better. " +
                "Choose your own coordinates, but place walls so the towers actually connect into a perimeter and put " +
                "the keep in the centre. Keep every targetName distinct.";

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
