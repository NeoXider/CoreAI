#if !COREAI_NO_LUA
#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// G8 — described-state selection (one part of the "director-AI / beyond the chat box" axis). Unlike
    /// G1–G7, which build a scene from a blank world, G8 gives the model a textual snapshot of an
    /// already-populated scene and asks it to act on the named existing objects:
    /// move/rotate/scale/parent/destroy specific ones, and wire conditional Lua from what it "observed".
    /// This measures whether the model can select actions from provided world state, not live scene sensing
    /// or sustained multi-turn direction. Grading is deterministic over the exact emitted commands.
    /// </summary>
    internal static class GameObserveActScenariosG8
    {
        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new TidyTheScene(),
                new SelectiveCleanup(),
                new StateDrivenRule()
            };
        }

        // --- base ------------------------------------------------------------------------------------

        private abstract class G8Scenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G8";
            public override int Difficulty => 4;
            public override bool CaptureScene => false; // acts on a described world; nothing to photograph
            public override int TokenBudget => 2600;
            public override double TimeBudgetMs => 35000;
            public override float TimeoutSeconds => 300f;

            protected virtual bool UsesLua => false;

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

            /// <summary>True if a command with this action targeted this exact object name.</summary>
            protected static bool ActedOn(BenchmarkEnvironment env, string action, string exactTarget)
            {
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.Action, action, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.TargetName?.Trim(), exactTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            protected static bool IsObservationOnly(RecordedWorldCommand command)
            {
                return string.Equals(command.Action, "list_objects", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(command.Action, "list_prefabs", StringComparison.OrdinalIgnoreCase);
            }

            protected static bool IsExactScaleOnlyChange(
                RecordedWorldCommand command, string exactTarget, float expectedScale)
            {
                if (!string.Equals(command.Action, "change", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(command.TargetName?.Trim(), exactTarget, StringComparison.OrdinalIgnoreCase)
                    || !command.HasScale
                    || command.HasPosition
                    || command.HasRotation
                    || !string.IsNullOrWhiteSpace(command.StringValue))
                {
                    return false;
                }

                float uniform = command.FloatValue > 0f ? command.FloatValue : 1f;
                float sx = command.ScaleX > 0f ? command.ScaleX : uniform;
                float sy = command.ScaleY > 0f ? command.ScaleY : uniform;
                float sz = command.ScaleZ > 0f ? command.ScaleZ : uniform;
                return Math.Abs(sx - expectedScale) < 1e-4f
                       && Math.Abs(sy - expectedScale) < 1e-4f
                       && Math.Abs(sz - expectedScale) < 1e-4f;
            }

            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run)
            {
                g.Add("used_tools", "issued at least one tool call", 5, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.Add("clean_tools", "no failed tool calls or invalid world commands", 10,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0,
                    dimension: BenchmarkDimension.ToolCorrectness,
                    detail: $"{run.FailedToolCalls} failed, {env.World.InvalidCommandCount} invalid");
            }
        }

        // --- scenarios -------------------------------------------------------------------------------

        /// <summary>
        /// Given a described scene, destroy exactly the two debris objects and leave everything else — a
        /// pure "read the state, act only on what was asked" task with an over-action penalty.
        /// </summary>
        private sealed class TidyTheScene : G8Scenario
        {
            public override string Id => "g8_tidy_scene";
            public override string Name => "Tidy the scene";

            public override string WhatItChecks =>
                "Reads a described scene and destroys exactly the two named debris objects, touching nothing else.";

            public override string Goal =>
                "You are tidying an existing scene. It currently contains these objects: 'Player', 'Tower', " +
                "'Bridge', 'Debris1', 'Debris2', 'Chest'. Two of them are junk that should be cleared away; " +
                "the rest are part of the level and must stay exactly as they are. Clean up the scene using " +
                "the world_command tool. Change nothing that should remain.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                bool d1 = ActedOn(env, "destroy", "Debris1");
                bool d2 = ActedOn(env, "destroy", "Debris2");
                int totalDestroys = env.World.Count("destroy");
                int nonObservationCommands = 0;
                bool onlyRequestedDestroys = true;
                foreach (RecordedWorldCommand command in env.World.Commands)
                {
                    if (IsObservationOnly(command))
                    {
                        continue;
                    }

                    nonObservationCommands++;
                    bool requestedDestroy = string.Equals(
                        command.Action, "destroy", StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(command.TargetName?.Trim(), "Debris1", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(command.TargetName?.Trim(), "Debris2", StringComparison.OrdinalIgnoreCase));
                    onlyRequestedDestroys &= requestedDestroy;
                }

                g.Add("removed_debris1", "destroyed 'Debris1'", 30, d1, true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("removed_debris2", "destroyed 'Debris2'", 30, d2,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("only_debris", "destroyed exactly the two debris, nothing else", 25,
                    d1 && d2 && totalDestroys == 2 && nonObservationCommands == 2 && onlyRequestedDestroys,
                    dimension: BenchmarkDimension.IntentSequence,
                    detail: $"{totalDestroys} destroys / {nonObservationCommands} mutating commands");

                // Penalise collateral damage: any destroy of a non-debris object.
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.Action, "destroy", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(c.TargetName?.Trim(), "Debris1", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(c.TargetName?.Trim(), "Debris2", StringComparison.OrdinalIgnoreCase))
                    {
                        g.Penalty($"destroyed a non-debris object '{c.TargetName}'", 15);
                    }
                }

                if (!onlyRequestedDestroys || nonObservationCommands != 2)
                {
                    g.Penalty("issued world mutations beyond the two requested debris destroys", 10);
                }

                if (d1 && d2 && totalDestroys == 2 && nonObservationCommands == 2 && onlyRequestedDestroys)
                {
                    g.Bonus = 5;
                }

                if (run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                return g;
            }
        }

        /// <summary>
        /// Conditional action over described state: raise the two towers that are "too short" and leave the
        /// correct one — the model must select targets from the described values, not act on all of them.
        /// </summary>
        private sealed class SelectiveCleanup : G8Scenario
        {
            public override string Id => "g8_selective_raise";
            public override string Name => "Selective raise";

            public override string WhatItChecks =>
                "Reads per-object state and acts only on the objects that fail a stated condition (two of three).";

            public override string Goal =>
                "Three towers stand in the scene. Their current sizes are: 'TowerA' is size 3.0, 'TowerB' is " +
                "size 1.0, 'TowerC' is size 0.5. In this game a tower only counts as a real tower once it is " +
                "at least size 2.0. Bring every undersized tower up to exactly size 2.0, and don't disturb any " +
                "tower that is already big enough. Use the world_command tool; add or remove nothing.";

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                bool raisedB = false;
                bool raisedC = false;
                bool touchedA = false;
                int nonObservationCommands = 0;
                foreach (RecordedWorldCommand command in env.World.Commands)
                {
                    if (IsObservationOnly(command))
                    {
                        continue;
                    }

                    nonObservationCommands++;
                    raisedB |= IsExactScaleOnlyChange(command, "TowerB", 2f);
                    raisedC |= IsExactScaleOnlyChange(command, "TowerC", 2f);
                    touchedA |= string.Equals(
                        command.TargetName?.Trim(), "TowerA", StringComparison.OrdinalIgnoreCase);
                }

                g.Add("raised_short_B", "scaled 'TowerB' (was 1.0)", 25, raisedB, true,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("raised_short_C", "scaled 'TowerC' (was 0.5)", 25, raisedC,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("left_tall_A", "did NOT scale the already-tall 'TowerA'", 20, !touchedA,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("exactly_two", "acted on exactly the two short towers", 20,
                    raisedB && raisedC && !touchedA && nonObservationCommands == 2,
                    dimension: BenchmarkDimension.IntentSequence,
                    detail: $"{nonObservationCommands} mutating commands");

                if (!raisedB || !raisedC || touchedA || nonObservationCommands != 2)
                {
                    g.Penalty("did not issue exactly two scale-only changes to TowerB/TowerC", 10);
                }

                if (raisedB && raisedC && !touchedA && nonObservationCommands == 2)
                {
                    g.Bonus = 5;
                }

                if (run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                return g;
            }
        }

        /// <summary>
        /// Observe described state and encode it as a Lua rule: the model must translate an observation
        /// ("enemies scale their HP with wave number") into a formula slot — reasoning over given state.
        /// </summary>
        private sealed class StateDrivenRule : G8Scenario
        {
            protected override bool UsesLua => true;

            public override string Id => "g8_state_rule";
            public override string Name => "State-driven rule";

            public override string WhatItChecks =>
                "Translates an observed world rule into a Lua logic slot, verified on derived inputs.";

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("enemy_hp");
            }

            public override string Goal =>
                "You are observing a wave-based arena. The design rule is: an enemy's HP is its base HP plus " +
                "20 per wave number beyond the first (wave 1 = base HP, wave 2 = base + 20, wave 3 = base + 40, " +
                "and so on). Using the execute_lua tool, install a logic slot named 'enemy_hp' that takes two " +
                "arguments (base, wave) and returns the HP for that wave: " +
                "logic_define('enemy_hp', function(base, wave) ... end). Work out the formula yourself. " +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                bool installed = env.Lua.LogicSlots.IsOverridden("enemy_hp");
                bool wave1 = env.Lua.TryNumber("enemy_hp", out double v1, 100.0, 1.0) && Math.Abs(v1 - 100.0) < 1e-6;
                // base + 20*(wave-1): (100,3)=140, (50,5)=130, (200,10)=380
                bool derived =
                    env.Lua.TryNumber("enemy_hp", out double v3, 100.0, 3.0) && Math.Abs(v3 - 140.0) < 1e-6 &&
                    env.Lua.TryNumber("enemy_hp", out double v5, 50.0, 5.0) && Math.Abs(v5 - 130.0) < 1e-6 &&
                    env.Lua.TryNumber("enemy_hp", out double v10, 200.0, 10.0) && Math.Abs(v10 - 380.0) < 1e-6;

                g.Add("installed", "enemy_hp slot installed", 15, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);
                g.Add("stated", "enemy_hp(base,1) == base", 20, wave1,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("derived", "enemy_hp(base,wave) == base + 20·(wave-1)", 60, derived,
                    dimension: BenchmarkDimension.Reasoning,
                    detail: "checked on (100,3)=140, (50,5)=130, (200,10)=380");

                if (installed && wave1 && derived)
                {
                    g.Bonus = 5;
                }

                if (run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                return g;
            }
        }
    }
}
#endif
#endif
