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
                "Build a castle showcase scene. Use the world_command tool only, action='spawn', prefabKey='Cube', " +
                "a DISTINCT targetName for every object, and the EXACT x,y,z below so the objects form a clean square " +
                "castle. First spawn ALL of these CORE objects, then add several of your own decorations.\n\n" +
                "CORE — spawn every one of these (use these exact coordinates):\n" +
                "- corner towers: Tower1 (-6,0,-6), Tower2 (6,0,-6), Tower3 (-6,0,6), Tower4 (6,0,6)\n" +
                "- flags high on the towers: Flag1 (-6,3,-6), Flag2 (6,3,-6), Flag3 (-6,3,6), Flag4 (6,3,6)\n" +
                "- front wall with a gateway gap: Wall1 (-3,0,-6), Wall2 (3,0,-6), Gate (0,0,-6)\n" +
                "- back wall: Wall3 (-3,0,6), Wall4 (0,0,6), Wall5 (3,0,6)\n" +
                "- left wall: Wall6 (-6,0,-3), Wall7 (-6,0,0), Wall8 (-6,0,3)\n" +
                "- right wall: Wall9 (6,0,-3), Wall10 (6,0,0), Wall11 (6,0,3)\n" +
                "- central keep: Keep (0,1,0)\n\n" +
                "THEN add 6 or more of your OWN extra objects with distinct names and sensible coordinates in the " +
                "-9..9 range to make it richer — for example trees outside the walls (Tree1, Tree2…), torches by the " +
                "gate, a bridge in front (Bridge at (0,0,-9)), banners, or courtyard props. Aim for 24+ objects total. " +
                "Keep every targetName distinct.";

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
