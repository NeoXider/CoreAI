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
            public override int TokenBudget => 2600;
            public override int MaxOutputTokens => 1600;
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
                "Build an elaborate castle showcase scene with full creative freedom. Use the world_command tool only, " +
                "with action='spawn', prefabKey='Cube', a distinct targetName for every object, and explicit x,y,z " +
                "coordinates so the objects form a readable castle in the world. Spawn AT LEAST 18 objects; 20 or more " +
                "is better. Keep coordinates roughly in the -8..8 range so the full scene fits in one screenshot.\n\n" +
                "Suggested layout, but you may improve it creatively:\n" +
                "- square perimeter walls around the castle, using names containing 'Wall';\n" +
                "- 4 corner towers, using names containing 'Tower';\n" +
                "- a central keep or castle block, using names containing 'Keep' or 'Castle';\n" +
                "- a front gate or door, using names containing 'Gate' or 'Door';\n" +
                "- flags on towers, using names containing 'Flag' and placed high with larger y values;\n" +
                "- optional moat or water around the castle, a bridge to the gate, trees outside, roofs, torches, " +
                "courtyard props, or decorative stones.\n\n" +
                "Use sensible coordinates to shape the scene. For example, walls can form a square perimeter near " +
                "x/z = +/-6, towers can sit at the four corners, the keep can sit at the center, the gate at the front, " +
                "and flags can sit above towers. Do not worry about exact names beyond keeping each targetName distinct.";

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
