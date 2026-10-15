#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        /// <summary>One described object of the <c>g8_extreme_siege_director</c> scene.</summary>
        internal sealed class ExtremeSceneRow
        {
            public ExtremeSceneRow(string name, string type, string faction, int hp, float x, float y, float z,
                string tag)
            {
                Name = name;
                Type = type;
                Faction = faction;
                Hp = hp;
                X = x;
                Y = y;
                Z = z;
                Tag = tag;
            }

            public string Name { get; }

            /// <summary>tower, turret, lamp, crate, banner or wall: the only field that decides what it is.</summary>
            public string Type { get; }

            /// <summary>ally or enemy for towers and turrets, "" otherwise.</summary>
            public string Faction { get; }

            /// <summary>Hit points of towers and turrets, 0 otherwise.</summary>
            public int Hp { get; }

            public float X { get; }
            public float Y { get; }
            public float Z { get; }

            /// <summary>"", "quest" (counts, never modified) or "ruin" (ignored everywhere, never modified).</summary>
            public string Tag { get; }
        }

        /// <summary>
        /// The described scene of <c>g8_extreme_siege_director</c>, interleaved by type on purpose. The goal
        /// text, the seeded names and the grader's expectation are all derived from this one table.
        /// </summary>
        internal static readonly ExtremeSceneRow[] ExtremeScene =
        {
            new("Gun_4", "turret", "enemy", 55, 22, 0, 12, ""),
            new("Wall_N1", "wall", "", 0, 0, 0, 40, ""),
            new("Lamp_A", "lamp", "", 0, 5, 0, 5, ""),
            new("Bastion_North", "tower", "ally", 120, 0, 0, 30, ""),
            new("Crate_7", "crate", "", 0, -27, 0, -17, ""),
            new("Banner_Red", "banner", "", 0, 4, 3, 34, ""),
            new("Gun_1", "turret", "enemy", 50, -14, 0, 8, ""),
            new("Wall_W2", "wall", "", 0, -30, 0, -10, ""),
            new("Crate_2", "crate", "", 0, 2, 0, 9, ""),
            new("Spire_Birch", "tower", "ally", 60, 10, 0, -20, ""),
            new("Lamp_E", "lamp", "", 0, 8, 0, 12, "quest"),
            new("Crate_Q", "crate", "", 0, -24, 0, -16, "quest"),
            new("Gun_9", "turret", "enemy", 50, 2, 0, -12, ""),
            new("Wall_Rubble", "wall", "", 0, 4, 0, 36, "ruin"),
            new("Banner_Gold", "banner", "", 0, 30, 3, 6, ""),
            new("Old_Spire", "tower", "ally", 10, 30, 0, 30, "ruin"),
            new("Crate_4", "crate", "", 0, -12, 0, 18, ""),
            new("Gun_6", "turret", "ally", 70, -18, 0, 6, ""),
            new("Beacon_Hollow", "lamp", "", 0, -25, 0, -20, ""),
            new("Wall_S2", "wall", "", 0, -12, 0, -44, ""),
            new("Keep_East", "tower", "ally", 150, 25, 0, 5, ""),
            new("Crate_5", "crate", "", 0, -9, 0, 13, "ruin"),
            new("Gun_2", "turret", "enemy", 45, 14, 0, -27, ""),
            new("Flag_Green", "banner", "", 0, -2, 3, -40, ""),
            new("Wall_E1", "wall", "", 0, 35, 0, 0, ""),
            new("Lantern_Crate", "crate", "", 0, -21, 0, -19, ""),
            new("Bastion_West", "tower", "ally", 101, -40, 0, -10, ""),
            new("Gun_10", "turret", "enemy", 45, -36, 0, -4, ""),
            new("Lamp_C", "lamp", "", 0, 18, 0, -8, ""),
            new("Crate_1", "crate", "", 0, 7, 0, 8, ""),
            new("Wall_W0", "wall", "", 0, -20, 0, 4, ""),
            new("Gun_5", "turret", "enemy", 40, -24, 0, -6, "quest"),
            new("Banner_Quest", "banner", "", 0, 40, 3, -30, "quest"),
            new("Spire_Ash", "tower", "ally", 80, -20, 0, 0, ""),
            new("Crate_8", "crate", "", 0, -22, 0, -22, ""),
            new("Wall_N2", "wall", "", 0, 10, 0, 40, ""),
            new("Gun_7", "turret", "enemy", 35, 12, 0, -16, "ruin"),
            new("Lamp_B", "lamp", "", 0, -10, 0, 15, ""),
            new("Gate_Tower_Red", "tower", "enemy", 40, -35, 0, 20, ""),
            new("Crate_3", "crate", "", 0, 11, 0, 5, ""),
            new("Banner_Blue", "banner", "", 0, -26, 3, 0, ""),
            new("Wall_E2", "wall", "", 0, 35, 0, 14, ""),
            new("Gun_3", "turret", "enemy", 60, -1, 0, -27, ""),
            new("Bastion_South", "tower", "ally", 95, -5, 0, -35, ""),
            new("Crate_6", "crate", "", 0, 18, 0, -14, ""),
            new("Wall_W1", "wall", "", 0, -30, 0, 10, ""),
            new("Gun_8", "turret", "enemy", 65, 28, 0, 26, ""),
            new("Wall_S1", "wall", "", 0, 0, 0, -45, "")
        };

        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new TidyTheScene(),
                new SelectiveCleanup(),
                new StateDrivenRule(),
                new ExtremeSiegeDirector()
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
                       || string.Equals(command.Action, "list_prefabs", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(command.Action, "list_animations", StringComparison.OrdinalIgnoreCase);
            }

            protected static bool IsExactScaleOnlyChange(
                RecordedWorldCommand command, string exactTarget, float expectedScale)
            {
                // WHY: the per-axis flags are checked as well as the aggregate ones: a recorded command may
                // carry an axis without the aggregate flag, and the recorder applies it either way.
                if (!string.Equals(command.Action, "change", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(command.TargetName?.Trim(), exactTarget, StringComparison.OrdinalIgnoreCase)
                    || !command.HasScale
                    || command.HasPosition || command.HasX || command.HasY || command.HasZ
                    || command.HasRotation || command.HasFx || command.HasFy || command.HasFz
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
                g.AddToolErrorCheckpoint("clean_tools", "no failed tool calls or invalid world commands", 10,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0,
                    scoresFailedToolCalls: true, scoresInvalidCommands: true,
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

            public override void Prepare(BenchmarkEnvironment env)
            {
                // WHY: list_objects must agree with the described existing scene.
                env.World.SeedObjects("Player", "Tower", "Bridge", "Debris1", "Debris2", "Chest");
            }

            public override string WhatItChecks =>
                "Reads a described scene and destroys exactly the two named debris objects, touching nothing else.";

            public override string Goal =>
                "You are tidying an existing scene. It currently contains these objects: 'Player', 'Tower', " +
                "'Bridge', 'Debris1', 'Debris2', 'Chest'. Debris1 and Debris2 are the junk to clear away; " +
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
                int extraMutations = 0;
                bool onlyRequestedDestroys = true;
                bool seenDebris1 = false;
                bool seenDebris2 = false;
                foreach (RecordedWorldCommand command in env.World.Commands)
                {
                    if (IsObservationOnly(command))
                    {
                        continue;
                    }

                    nonObservationCommands++;
                    bool isDestroy = string.Equals(command.Action, "destroy", StringComparison.OrdinalIgnoreCase);
                    bool onDebris1 = string.Equals(command.TargetName?.Trim(), "Debris1",
                        StringComparison.OrdinalIgnoreCase);
                    bool onDebris2 = string.Equals(command.TargetName?.Trim(), "Debris2",
                        StringComparison.OrdinalIgnoreCase);
                    bool requestedDestroy = isDestroy && (onDebris1 || onDebris2);
                    onlyRequestedDestroys &= requestedDestroy;
                    if (isDestroy && onDebris1 && !seenDebris1)
                    {
                        seenDebris1 = true;
                    }
                    else if (isDestroy && onDebris2 && !seenDebris2)
                    {
                        seenDebris2 = true;
                    }
                    else
                    {
                        extraMutations++;
                    }
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

                // WHY: charged per extra mutation (one costs what the former flat charge did) so a run that
                // rewrites half the scene is not priced like one stray command.
                if (!onlyRequestedDestroys || nonObservationCommands > 2)
                {
                    g.Penalty($"issued {extraMutations} world mutation(s) beyond the two requested debris destroys",
                        10 * Math.Max(1, extraMutations));
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

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.World.SeedObjects("TowerA", "TowerB", "TowerC");
            }

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
                int wrongMutations = 0;
                foreach (RecordedWorldCommand command in env.World.Commands)
                {
                    if (IsObservationOnly(command))
                    {
                        continue;
                    }

                    nonObservationCommands++;
                    bool exactB = !raisedB && IsExactScaleOnlyChange(command, "TowerB", 2f);
                    bool exactC = !raisedC && IsExactScaleOnlyChange(command, "TowerC", 2f);
                    raisedB |= exactB;
                    raisedC |= exactC;
                    if (!exactB && !exactC)
                    {
                        wrongMutations++;
                    }

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

                // WHY: charged per wrong mutation (a single one costs what the former flat charge did).
                if (!raisedB || !raisedC || touchedA || nonObservationCommands != 2)
                {
                    g.Penalty("did not issue exactly two scale-only changes to TowerB/TowerC " +
                              $"({wrongMutations} wrong mutation(s))", 10 * Math.Max(1, wrongMutations));
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

        /// <summary>
        /// Extreme tier: a 48-object described scene and three rule families that each need real computation
        /// (a faction average with a strict comparison, inclusive distance thresholds, a nearest-two search with
        /// a stated tie-break) plus two tags that change what counts and what may be modified. The expected
        /// mutations are derived from <see cref="ExtremeScene"/>; every other mutation, and every polluted field
        /// on an expected one, is a violation. Each rule family scores separately for partial credit.
        /// </summary>
        private sealed class ExtremeSiegeDirector : G8Scenario
        {
            private const float Tolerance = 1e-3f;

            public override string Id => "g8_extreme_siege_director";
            public override string Name => "Extreme: Siege director";
            public override bool IsExtreme => true;
            public override int Difficulty => 5;
            public override float TimeoutSeconds => 480f;
            public override int TokenBudget => 12000;
            public override double TimeBudgetMs => 240000;

            public override string WhatItChecks =>
                "Derives ten exact mutations from a 48-object described scene (faction average, inclusive " +
                "distances, nearest-two with tie-break, ruin/quest tags) and issues nothing else.";

            public override void Prepare(BenchmarkEnvironment env)
            {
                // WHY: list_objects and coreai_world_pos must report the positions the table describes.
                foreach (ExtremeSceneRow row in ExtremeScene)
                {
                    env.World.SeedObject(row.Name, new UnityEngine.Vector3(row.X, row.Y, row.Z));
                }
            }

            public override string Goal
            {
                get
                {
                    StringBuilder sb = new();
                    sb.Append(
                        "You direct an existing siege scene. The table below is its authoritative state. Names are " +
                        "exact, and the type alone decides what an object is (names mean nothing). Every distance " +
                        "is the straight-line distance in the horizontal x-z plane (ignore y); 'within D' means a " +
                        "distance <= D (inclusive).\n" +
                        "Tags: 'ruin' = rubble: ignore the object in every computation and never modify it. " +
                        "'quest' = the object counts normally in every computation but must never be modified, " +
                        "even when a rule selects it.\n" +
                        "Apply exactly these three rules, then stop:\n" +
                        "1. Turrets. Let H be the mean hp of all allied towers. An allied tower is weakened when " +
                        "its hp is strictly below H. Destroy (world_command action='destroy') every enemy turret " +
                        "that is within 10 of at least one weakened allied tower.\n" +
                        "2. Lamps. For every lamp let n be the number of crates within 5 of it. If n >= 1, set the " +
                        "lamp's uniform scale to 1 + n with action='change' passing targetName and scale only. If " +
                        "n = 0, leave the lamp alone. Every lamp currently has scale 1.\n" +
                        "3. Banners. Move every banner to the midpoint of its two nearest walls: rank the walls by " +
                        "distance to the banner; when two walls are equally distant, the one whose name sorts " +
                        "first (ordinal, case-sensitive) ranks nearer. Use action='change' passing targetName, x " +
                        "and z only: no y, rotation, scale or parent (each banner keeps its height).\n" +
                        "Issue no other mutation: no spawns, no other change or destroy, and never repeat an " +
                        "identical tool call. Observing with list_objects is allowed: it reports the same names " +
                        "and positions, while type, faction, hp and tags exist only in this table.\n" +
                        "Every failed tool call (a rejected, refused or rate-limited call) costs points. You have " +
                        "about 8 minutes for this task.\n\n" +
                        "SCENE (" + ExtremeScene.Length + " objects):\n");
                    for (int i = 0; i < ExtremeScene.Length; i++)
                    {
                        sb.Append(RenderRow(ExtremeScene[i], i)).Append('\n');
                    }

                    return sb.ToString();
                }
            }

            /// <summary>Three row layouts in rotation: irregular on purpose, never ambiguous.</summary>
            private static string RenderRow(ExtremeSceneRow row, int index)
            {
                string tag = row.Tag.Length == 0 ? "none" : row.Tag;
                string faction = row.Faction.Length == 0 ? "-" : row.Faction;
                string hp = row.Hp > 0 ? row.Hp.ToString(CultureInfo.InvariantCulture) : "-";
                string x = Num(row.X);
                string y = Num(row.Y);
                string z = Num(row.Z);
                switch (index % 3)
                {
                    case 0:
                        return $"{row.Name} | {row.Type} | faction {faction} | hp {hp} | x={x} y={y} z={z} | tag {tag}";
                    case 1:
                        return $"{row.Type} \"{row.Name}\" at (x {x}, y {y}, z {z}); faction {faction}; hp {hp}; " +
                               $"tags [{tag}]";
                    default:
                        return $"'{row.Name}': type={row.Type} xyz=({x}, {y}, {z}) faction={faction} hp={hp} tag={tag}";
                }
            }

            private static string Num(float value)
            {
                return value.ToString("0.##", CultureInfo.InvariantCulture);
            }

            /// <summary>The exact mutations the three rules select.</summary>
            private sealed class SiegeExpectation
            {
                public readonly HashSet<string> Destroys = new(StringComparer.OrdinalIgnoreCase);
                public readonly Dictionary<string, float> Scales = new(StringComparer.OrdinalIgnoreCase);
                public readonly Dictionary<string, (float X, float Z)> Moves = new(StringComparer.OrdinalIgnoreCase);

                public int Count => Destroys.Count + Scales.Count + Moves.Count;
            }

            private static double DistanceSquared(ExtremeSceneRow a, ExtremeSceneRow b)
            {
                double dx = a.X - b.X;
                double dz = a.Z - b.Z;
                return dx * dx + dz * dz;
            }

            private static bool Counts(ExtremeSceneRow row)
            {
                return row.Tag != "ruin";
            }

            private static bool Modifiable(ExtremeSceneRow row)
            {
                return row.Tag != "ruin" && row.Tag != "quest";
            }

            private static SiegeExpectation Evaluate()
            {
                SiegeExpectation expected = new();

                List<ExtremeSceneRow> alliedTowers = new();
                double hpSum = 0;
                foreach (ExtremeSceneRow row in ExtremeScene)
                {
                    if (row.Type == "tower" && row.Faction == "ally" && Counts(row))
                    {
                        alliedTowers.Add(row);
                        hpSum += row.Hp;
                    }
                }

                double meanHp = alliedTowers.Count > 0 ? hpSum / alliedTowers.Count : 0;
                List<ExtremeSceneRow> weakened = alliedTowers.FindAll(t => t.Hp < meanHp);

                foreach (ExtremeSceneRow row in ExtremeScene)
                {
                    if (row.Type == "turret" && row.Faction == "enemy" && Modifiable(row)
                        && weakened.Exists(t => DistanceSquared(row, t) <= 100.0))
                    {
                        expected.Destroys.Add(row.Name);
                    }

                    if (row.Type == "lamp" && Modifiable(row))
                    {
                        int crates = 0;
                        foreach (ExtremeSceneRow other in ExtremeScene)
                        {
                            if (other.Type == "crate" && Counts(other) && DistanceSquared(row, other) <= 25.0)
                            {
                                crates++;
                            }
                        }

                        if (crates >= 1)
                        {
                            expected.Scales[row.Name] = 1 + crates;
                        }
                    }

                    if (row.Type == "banner" && Modifiable(row))
                    {
                        List<ExtremeSceneRow> walls = new();
                        foreach (ExtremeSceneRow other in ExtremeScene)
                        {
                            if (other.Type == "wall" && Counts(other))
                            {
                                walls.Add(other);
                            }
                        }

                        ExtremeSceneRow banner = row;
                        walls.Sort((a, b) =>
                        {
                            int byDistance = DistanceSquared(banner, a).CompareTo(DistanceSquared(banner, b));
                            return byDistance != 0 ? byDistance : string.CompareOrdinal(a.Name, b.Name);
                        });
                        if (walls.Count >= 2)
                        {
                            expected.Moves[row.Name] = ((walls[0].X + walls[1].X) / 2f, (walls[0].Z + walls[1].Z) / 2f);
                        }
                    }
                }

                return expected;
            }

            /// <summary>
            /// The world action of a suppressed repeat, read from its payload (an <c>Action</c> field, or the
            /// <c>action</c> inside an <c>arguments</c> object or JSON string), or "" when the payload names none.
            /// The current duplicate payload carries neither, so such a repeat is charged but cannot be
            /// classified.
            /// </summary>
            private static string SuppressedAction(LlmToolCallTrace trace)
            {
                if (string.IsNullOrWhiteSpace(trace.Detail))
                {
                    return "";
                }

                try
                {
                    JObject payload = JObject.Parse(trace.Detail);
                    JToken action = payload.GetValue("Action", StringComparison.OrdinalIgnoreCase);
                    JToken arguments = payload.GetValue("arguments", StringComparison.OrdinalIgnoreCase);
                    if (action == null && arguments is { Type: JTokenType.String })
                    {
                        arguments = JObject.Parse((string)arguments);
                    }

                    if (action == null && arguments is JObject argumentObject)
                    {
                        action = argumentObject.GetValue("action", StringComparison.OrdinalIgnoreCase);
                    }

                    return action is { Type: JTokenType.String }
                        ? ((string)action).Trim().ToLowerInvariant()
                        : "";
                }
                catch (JsonException)
                {
                    return "";
                }
            }

            private static bool IsObservationAction(string action)
            {
                return action is "list_objects" or "list_prefabs" or "list_animations";
            }

            /// <summary>A change that sets exactly x and z: no y, rotation, scale or parent.</summary>
            private static bool IsExactPlanarMove(RecordedWorldCommand command, string exactTarget, float x, float z)
            {
                return string.Equals(command.Action, "change", StringComparison.OrdinalIgnoreCase)
                       && string.Equals(command.TargetName?.Trim(), exactTarget, StringComparison.OrdinalIgnoreCase)
                       && command.HasX && command.HasZ && !command.HasY
                       && Math.Abs(command.X - x) < Tolerance && Math.Abs(command.Z - z) < Tolerance
                       && !command.HasRotation && !command.HasFx && !command.HasFy && !command.HasFz
                       && !command.HasScale && command.FloatValue <= 0f
                       && command.ScaleX <= 0f && command.ScaleY <= 0f && command.ScaleZ <= 0f
                       && string.IsNullOrWhiteSpace(command.StringValue);
            }

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                SiegeExpectation expected = Evaluate();

                HashSet<string> protectedNames = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> lampNames = new(StringComparer.OrdinalIgnoreCase);
                foreach (ExtremeSceneRow row in ExtremeScene)
                {
                    if (!Modifiable(row))
                    {
                        protectedNames.Add(row.Name);
                    }

                    if (row.Type == "lamp")
                    {
                        lampNames.Add(row.Name);
                    }
                }

                HashSet<string> matched = new(StringComparer.OrdinalIgnoreCase);
                bool destroyOutsideSelection = false;
                bool lampOutsideSelection = false;
                int protectedHits = 0;
                int wrongDestroys = 0;
                int otherViolations = 0;
                foreach (RecordedWorldCommand command in env.World.Commands)
                {
                    if (IsObservationOnly(command))
                    {
                        continue;
                    }

                    string target = command.TargetName?.Trim() ?? "";
                    bool carriesParent = string.Equals(command.Action, "spawn", StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(command.Action, "change", StringComparison.OrdinalIgnoreCase);
                    string parent = carriesParent ? command.StringValue?.Trim() ?? "" : "";
                    bool isDestroy = string.Equals(command.Action, "destroy", StringComparison.OrdinalIgnoreCase);

                    destroyOutsideSelection |= isDestroy && !expected.Destroys.Contains(target);
                    lampOutsideSelection |= (lampNames.Contains(target) && !expected.Scales.ContainsKey(target))
                                            || (lampNames.Contains(parent) && !expected.Scales.ContainsKey(parent));

                    bool exact = !matched.Contains(target)
                                 && ((isDestroy && expected.Destroys.Contains(target))
                                     || (expected.Scales.TryGetValue(target, out float scale)
                                         && IsExactScaleOnlyChange(command, target, scale))
                                     || (expected.Moves.TryGetValue(target, out (float X, float Z) spot)
                                         && IsExactPlanarMove(command, target, spot.X, spot.Z)));
                    if (exact)
                    {
                        matched.Add(target);
                        continue;
                    }

                    if (protectedNames.Contains(target) || protectedNames.Contains(parent))
                    {
                        protectedHits++;
                    }
                    else if (isDestroy && !expected.Destroys.Contains(target))
                    {
                        wrongDestroys++;
                    }
                    else
                    {
                        otherViolations++;
                    }
                }

                bool allDestroys = true;
                foreach (string name in expected.Destroys)
                {
                    allDestroys &= matched.Contains(name);
                }

                bool allScales = true;
                foreach (string name in expected.Scales.Keys)
                {
                    allScales &= matched.Contains(name);
                }

                bool allMoves = true;
                foreach (string name in expected.Moves.Keys)
                {
                    allMoves &= matched.Contains(name);
                }

                // WHY: an identical call in a later roundtrip is suppressed before it reaches the recorder, so
                // only its duplicate trace shows the repeat the goal forbids. Every one is charged; only a
                // repeat known to be a mutation also breaks the exact mutation set.
                int suppressedRepeats = 0;
                int suppressedMutations = 0;
                foreach (CapturedTurn turn in run.CapturedTurns)
                {
                    foreach (LlmToolCallTrace call in turn.Tools)
                    {
                        if (!string.Equals(call.Source, "duplicate", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        suppressedRepeats++;
                        string action = SuppressedAction(call);
                        if (action.Length > 0 && !IsObservationAction(action))
                        {
                            suppressedMutations++;
                        }
                    }
                }

                int violations = protectedHits + wrongDestroys + otherViolations;
                bool exactSet = matched.Count == expected.Count && violations == 0 && suppressedMutations == 0;

                g.Add("used_tools", "issued at least one tool call", 2, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.AddToolErrorCheckpoint("clean_tools", "no failed tool calls or invalid world commands", 4,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0,
                    scoresFailedToolCalls: true, scoresInvalidCommands: true,
                    detail: $"{run.FailedToolCalls} failed, {env.World.InvalidCommandCount} invalid");
                g.Add("core_task", "performed at least one exactly selected mutation", 6, matched.Count >= 1, true,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: $"{matched.Count}/{expected.Count} exact");
                g.Add("turrets_selected",
                    $"destroyed every selected enemy turret ({string.Join(", ", expected.Destroys)})", 16,
                    allDestroys, dimension: BenchmarkDimension.Reasoning);
                g.Add("turrets_precise", "destroyed nothing outside the selected turrets", 8,
                    !destroyOutsideSelection, dimension: BenchmarkDimension.Reasoning);
                g.Add("lamps_scaled", "scaled every selected lamp to exactly 1 + n, scale only", 16, allScales,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("lamps_precise", "touched no lamp outside the selection", 6, !lampOutsideSelection,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("banners_moved", "moved every banner to its exact wall midpoint, x and z only", 16, allMoves,
                    dimension: BenchmarkDimension.Reasoning);
                g.Constraint("protected_untouched", "never modified a quest- or ruin-tagged object", 10,
                    protectedHits > 0, 10, protectedHits);
                g.Add("exact_mutation_set", $"issued exactly the {expected.Count} selected mutations and nothing else",
                    12, exactSet, dimension: BenchmarkDimension.IntentSequence,
                    detail: $"{matched.Count} exact, {violations} other, {suppressedMutations} suppressed");

                if (wrongDestroys > 0)
                {
                    g.Penalty($"destroyed {wrongDestroys} object(s) no rule selected", 8 * wrongDestroys);
                }

                if (otherViolations > 0)
                {
                    g.Penalty($"{otherViolations} extra, repeated or polluted mutation(s)", 4 * otherViolations);
                }

                if (suppressedRepeats > 0)
                {
                    g.Penalty($"{suppressedRepeats} identical call(s) repeated and suppressed", 4 * suppressedRepeats);
                }

                if (run.ToolCalls == 0 || matched.Count == 0)
                {
                    g.HardCap = 40;
                }

                if (exactSet && suppressedRepeats == 0 && run.FailedToolCalls == 0 &&
                    env.World.InvalidCommandCount == 0)
                {
                    g.Bonus = 8;
                }

                return g;
            }
        }
    }
}
#endif
#endif
