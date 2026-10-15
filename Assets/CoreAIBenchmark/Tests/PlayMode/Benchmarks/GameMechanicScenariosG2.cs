#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Benchmarking;
using Lua;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// G2 — runtime mechanic authoring. The game declares named <see cref="LuaLogicSlots"/>; the model
    /// must install Lua overrides via <c>execute_lua</c> (<c>logic_define</c>) to satisfy a behavioral
    /// spec. Graded by EXECUTING the installed slot on a battery of inputs — including <b>hidden</b>
    /// samples not shown in the prompt — so a piecewise/constant fake that only matches the examples does
    /// not pass. Checkpoints are tagged with the <see cref="BenchmarkDimension"/> they measure.
    /// </summary>
    internal static class GameMechanicScenariosG2
    {
        /// <summary>
        /// Run rules every extreme-tier logic goal ends with (the G2, G3 and G4 extreme scenarios): the time box,
        /// the per-call instruction budget that uninstalls a slot, the cost of failed tool calls and the
        /// execute_lua rate limit, so a model that self-tests knows to report values instead of raising.
        /// </summary>
        internal const string ExtremeRunRules =
            "RUN RULES: You have about 8 minutes for this task. One slot call may run at most 200,000 Lua VM " +
            "instructions; a call that exceeds it or raises uninstalls the slot, which fails every later check, " +
            "so never error on valid input. Every failed tool call (a Lua chunk that raises, a rejected or " +
            "rate-limited call) costs points: check values by returning a string, never by raising. execute_lua " +
            "accepts at most 20 calls per 60 seconds.\n";

        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new FlatDamageBuff(),
                new LevelScaledDamage(),
                new ScoreWinCondition(),
                new MultiArgDamage(),
                new CraftingRules(),
                new ExtremeDamagePipeline()
            };
        }

        private abstract class LuaSlotScenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G2";
            public override int Difficulty => 2;
            protected abstract string Slot { get; }

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot(Slot);
            }

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.LuaTool())
                    .WithMaxOutputTokens(MaxOutputTokens)
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
            }

            protected bool NumberIs(BenchmarkEnvironment env, double expected, params object[] args)
            {
                return env.Lua.TryNumber(Slot, out double v, args) && System.Math.Abs(v - expected) < 1e-6;
            }

            protected bool BoolIs(BenchmarkEnvironment env, bool expected, params object[] args)
            {
                return env.Lua.TryBool(Slot, out bool v, args) && v == expected;
            }

            /// <summary>Common Tool-correctness checkpoints: the model ran Lua, with no failed call or execution.</summary>
            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run,
                double ranWeight = 5, double cleanWeight = 5)
            {
                g.Add("ran_lua", "called execute_lua at least once", ranWeight, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                // WHY a declared tool-error checkpoint: a plain checkpoint left the harness believing no
                // checkpoint scored failed calls, so a raising chunk failed clean_lua AND drew the harness's
                // failed-call penalty, charging G2 twice for what G3/G4 charge once.
                bool clean = run.FailedToolCalls == 0 && env.Lua.FailedExecutions == 0;
                g.AddToolErrorCheckpoint("clean_lua", "no failed tool calls or Lua executions", cleanWeight, clean,
                    scoresFailedToolCalls: true, scoresInvalidCommands: false,
                    detail: clean
                        ? null
                        : $"{run.FailedToolCalls} failed calls, {env.Lua.FailedExecutions} failed Lua runs");
            }
        }

        private sealed class FlatDamageBuff : LuaSlotScenario
        {
            public override string Id => "g2_flat_damage";
            public override string Name => "Flat damage buff";
            public override int Difficulty => 1;
            protected override string Slot => "calculate_damage";

            public override string Goal =>
                "The game has a runtime logic slot 'calculate_damage'. Players say the game is too easy. " +
                "Using the execute_lua tool, run exactly:\n" +
                "logic_define('calculate_damage', function() return 50 end)\n" +
                "so that calculate_damage() returns 50. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                bool installed = env.Lua.LogicSlots.IsOverridden(Slot);
                bool returns50 = NumberIs(env, 50);

                AddToolHygiene(g, env, run);
                g.Add("override_installed", "calculate_damage override installed", 30, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);
                g.Add("returns_50", "calculate_damage() returns 50", 60, returns50, true,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: installed ? null : "slot was never overridden");

                if (!installed && run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                if (returns50)
                {
                    g.Bonus = 4;
                }

                return g;
            }
        }

        private sealed class LevelScaledDamage : LuaSlotScenario
        {
            public override string Id => "g2_level_damage";
            public override string Name => "Level-scaled damage";
            protected override string Slot => "calculate_damage";

            public override string Goal =>
                "The game has a logic slot 'calculate_damage'. Using execute_lua, define it so damage equals " +
                "10 times the level passed as the first argument. Run exactly this shape:\n" +
                "logic_define('calculate_damage', function(level) return 10 * level end)\n" +
                "For example level 1 -> 10, level 5 -> 50. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "override installed", 10, env.Lua.LogicSlots.IsOverridden(Slot), true,
                    dimension: BenchmarkDimension.IntentSequence);
                g.Add("level_1", "damage(1) == 10", 10, NumberIs(env, 10, 1.0),
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("level_5", "damage(5) == 50", 10, NumberIs(env, 50, 5.0), true,
                    dimension: BenchmarkDimension.TaskCompletion);

                bool hidden = NumberIs(env, 0, 0.0) && NumberIs(env, 20, 2.0) && NumberIs(env, 30, 3.0)
                              && NumberIs(env, 70, 7.0) && NumberIs(env, 110, 11.0);
                g.Add("hidden_samples", "damage(0,2,3,7,11) all == 10*level", 45, hidden, true,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: hidden ? null : "fails on inputs not shown in the prompt");

                bool monotonic = env.Lua.TryNumber(Slot, out double d1, 1.0)
                                 && env.Lua.TryNumber(Slot, out double d2, 2.0) && d2 > d1;
                g.Add("monotonic", "damage increases with level", 10, monotonic,
                    dimension: BenchmarkDimension.TaskCompletion);

                if (hidden)
                {
                    g.Bonus = 4;
                }

                return g;
            }
        }

        private sealed class ScoreWinCondition : LuaSlotScenario
        {
            public override string Id => "g2_win_condition";
            public override string Name => "Score win condition";
            protected override string Slot => "win_condition";

            public override string Goal =>
                "The game has a boolean logic slot 'win_condition'. Using execute_lua, define it so the player " +
                "wins exactly when their score (first argument) is at least 100. Run exactly this shape:\n" +
                "logic_define('win_condition', function(score) return score >= 100 end)\n" + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "override installed", 10, env.Lua.LogicSlots.IsOverridden(Slot), true,
                    dimension: BenchmarkDimension.IntentSequence);
                g.Add("at_true", "win(100) == true", 15, BoolIs(env, true, 100.0), true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("below_false", "win(99) == false", 15, BoolIs(env, false, 99.0), true,
                    dimension: BenchmarkDimension.TaskCompletion);

                // WHY 99.5, 100.5 and -5: every other sample is an integer, so 'score > 99' passed them all.
                bool hidden = BoolIs(env, false, 0.0) && BoolIs(env, false, 50.0) && BoolIs(env, true, 101.0)
                              && BoolIs(env, true, 1000.0) && BoolIs(env, false, 99.5)
                              && BoolIs(env, true, 100.5) && BoolIs(env, false, -5.0);
                g.Add("hidden_samples", "win(0,50,99.5,-5)=false, win(100.5,101,1000)=true", 45, hidden, true,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: hidden ? null : "fails on inputs not shown in the prompt");

                if (hidden)
                {
                    g.Bonus = 4;
                }

                return g;
            }
        }

        private sealed class MultiArgDamage : LuaSlotScenario
        {
            public override string Id => "g2_multi_arg_damage";
            public override string Name => "Multi-arg damage formula";
            public override int Difficulty => 3;
            protected override string Slot => "calculate_damage";
            public override int TokenBudget => 1800;
            public override float TimeoutSeconds => 240f;

            public override string Goal =>
                "The game has a logic slot 'calculate_damage' that receives three arguments: base, level, armor. " +
                "Using execute_lua, define it so the result is base + level*10 - armor, but never below 0. " +
                "Run a shape like:\n" +
                "logic_define('calculate_damage', function(base, level, armor) " +
                "local d = base + level*10 - armor; if d < 0 then d = 0 end; return d end)\n" + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "override installed", 10, env.Lua.LogicSlots.IsOverridden(Slot), true,
                    dimension: BenchmarkDimension.IntentSequence);
                g.Add("basic", "damage(10,1,0) == 20", 20, NumberIs(env, 20, 10.0, 1.0, 0.0), true,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("with_armor", "damage(10,2,5) == 25", 20, NumberIs(env, 25, 10.0, 2.0, 5.0),
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("hidden", "damage(0,3,1)=29, (5,1,3)=12, (12,0,4)=8, (1,10,0)=101", 20,
                    NumberIs(env, 29, 0.0, 3.0, 1.0) && NumberIs(env, 12, 5.0, 1.0, 3.0)
                                                     && NumberIs(env, 8, 12.0, 0.0, 4.0)
                                                     && NumberIs(env, 101, 1.0, 10.0, 0.0),
                    dimension: BenchmarkDimension.TaskCompletion);
                // WHY (5,2,40) and (7,4,47): the clamp was only probed with base = level = 0, never with a
                // positive base and level outweighed by armor, nor at the exact-zero boundary.
                g.Add("clamped", "damage(0,0,50)=0, (5,2,40)=0, (7,4,47)=0 (never negative)", 10,
                    NumberIs(env, 0, 0.0, 0.0, 50.0) && NumberIs(env, 0, 5.0, 2.0, 40.0)
                                                     && NumberIs(env, 0, 7.0, 4.0, 47.0),
                    true, dimension: BenchmarkDimension.TaskCompletion);

                if (NumberIs(env, 20, 10.0, 1.0, 0.0) && NumberIs(env, 0, 0.0, 0.0, 50.0))
                {
                    g.Bonus = 5;
                }

                return g;
            }
        }

        /// <summary>
        /// Game-mechanic test (a harder crafting system, inspired by the crafting tests): the model
        /// authors a deterministic recipe resolver in Lua. Graded for correctness on every listed recipe,
        /// rejection of unknown pairs (hidden), AND determinism (same inputs twice → identical output).
        /// </summary>
        private sealed class CraftingRules : LuaSlotScenario
        {
            public override string Id => "g2_crafting_rules";
            public override string Name => "Crafting rules engine";
            public override int Difficulty => 3;
            protected override string Slot => "craft_result";
            public override int TokenBudget => 2400;
            public override double TimeBudgetMs => 35000;
            public override float TimeoutSeconds => 300f;

            public override string Goal =>
                "The game has a logic slot 'craft_result(a, b)' that returns the item crafted from two " +
                "ingredients, or the string 'none' if the pair is not a valid recipe. The recipes are:\n" +
                "  wood  + wood = plank\n" +
                "  plank + plank = table\n" +
                "  iron  + coal = steel\n" +
                "  wood  + coal = torch\n" +
                "Recipes are order-independent (wood+coal and coal+wood both give torch). " +
                "Using execute_lua, define the slot, for example:\n" +
                "logic_define('craft_result', function(a, b) ... return 'plank' ... end)\n" + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "craft_result override installed", 10, env.Lua.LogicSlots.IsOverridden(Slot),
                    true, dimension: BenchmarkDimension.IntentSequence);

                bool recipes = Str(env, "plank", "wood", "wood") && Str(env, "table", "plank", "plank")
                                                                 && Str(env, "steel", "iron", "coal") &&
                                                                 Str(env, "torch", "wood", "coal");
                g.Add("recipes", "all four recipes craft correctly", 35, recipes, true,
                    dimension: BenchmarkDimension.TaskCompletion);

                bool symmetric = Str(env, "torch", "coal", "wood") && Str(env, "steel", "coal", "iron");
                g.Add("order_independent", "ingredient order does not matter", 15, symmetric,
                    dimension: BenchmarkDimension.TaskCompletion);

                // WHY plank+wood and steel+coal: a resolver that keys on one ingredient ("any plank makes a
                // table", "any coal makes steel or a torch") passed the three pairs probed before.
                bool rejects = Str(env, "none", "wood", "iron") && Str(env, "none", "coal", "coal")
                                                                && Str(env, "none", "stone", "stone")
                                                                && Str(env, "none", "plank", "wood")
                                                                && Str(env, "none", "steel", "coal")
                                                                && Str(env, "none", "iron", "iron");
                g.Add("rejects_unknown", "unknown pairs return 'none' (hidden)", 20, rejects, true,
                    dimension: BenchmarkDimension.TaskCompletion);

                // Determinism: the same inputs must yield the same output across repeated calls.
                bool deterministic = SameTwice(env, "wood", "wood") && SameTwice(env, "iron", "coal")
                                                                    && SameTwice(env, "stone", "stone");
                g.Add("deterministic", "identical inputs give identical outputs", 20, deterministic,
                    true, dimension: BenchmarkDimension.Determinism,
                    detail: deterministic ? null : "output changed between identical calls");

                if (recipes && rejects && deterministic)
                {
                    g.Bonus = 6;
                }

                return g;
            }

            private bool Str(BenchmarkEnvironment env, string expected, string a, string b)
            {
                return env.Lua.TryString(Slot, out string v, a, b)
                       && string.Equals((v ?? "").Trim(), expected, System.StringComparison.OrdinalIgnoreCase);
            }

            private bool SameTwice(BenchmarkEnvironment env, string a, string b)
            {
                bool ok1 = env.Lua.TryString(Slot, out string v1, a, b);
                bool ok2 = env.Lua.TryString(Slot, out string v2, a, b);
                return ok1 && ok2 && string.Equals(v1 ?? "", v2 ?? "", System.StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Extreme tier: one slot resolves a whole multi-hit attack — level scaling, deterministic crits,
        /// percentage-then-flat armor penetration, an element matrix with an immunity, clamped resistances,
        /// guard, a minimum that immunity overrides, shield absorption with shatter, overkill — and returns a
        /// result table. Every rule is stated exactly in the goal; the difficulty is the number of
        /// interacting steps. Graded on 69 hidden attacks split into rule families, each family keeping the
        /// other mechanics neutral, so one wrong rule costs only its family. Expected values come from
        /// <see cref="DamagePipelineReference"/>, a C# implementation independent of any Lua solution.
        /// </summary>
        private sealed class ExtremeDamagePipeline : LuaSlotScenario
        {
            public override string Id => "g2_extreme_damage_pipeline";
            public override string Name => "Extreme: combat damage pipeline";
            public override int Difficulty => 5;
            public override bool IsExtreme => true;
            public override int TokenBudget => 9000;
            public override double TimeBudgetMs => 240000;
            public override float TimeoutSeconds => 480f;
            public override int? MaxToolCallRoundtripsOverride => 40;
            protected override string Slot => "resolve_attack";

            public override string WhatItChecks =>
                "9-step multi-hit damage pipeline (crit, armor pen, element matrix, resist caps, guard, shield " +
                "shatter, overkill) on 69 hidden attacks graded per rule family";

            public override string Goal =>
                "EXTREME TASK. The game has a logic slot 'resolve_attack' that resolves one attack. Install it " +
                "with execute_lua as logic_define('resolve_attack', function(attacker, defender, skill) ... end). " +
                "The sandbox is Lua 5.2: there is no // operator, no bitwise operator and no integer subtype " +
                "(every number is a double), so use math.floor. Below, floor() is math.floor and 'clamped to " +
                "[lo, hi]' means min(max(x, lo), hi).\n" +
                "INPUT (three tables; every number is an integer):\n" +
                "- attacker: atk >= 0, level, crit_chance (0-100), crit_mult (percent, >= 100), pen_pct (0-100), " +
                "pen_flat >= 0.\n" +
                "- defender: hp >= 1, shield >= 0, armor >= 0, level, element ('fire', 'water', 'nature', " +
                "'neutral' or 'ghost'), resist (table: attack element name -> percent, may be negative or above " +
                "100), guarding (boolean).\n" +
                "- skill: power (percent, >= 0), element ('physical', 'fire', 'water' or 'nature'), hits >= 1, " +
                "true_damage (boolean), seed >= 0.\n" +
                "These fields may be nil and then default to: crit_chance 0, crit_mult 150, pen_pct 0, " +
                "pen_flat 0, shield 0, resist = a table without entries, a missing resist entry = 0, " +
                "guarding false, skill.element 'physical', hits 1, true_damage false, seed 0. Every other field " +
                "is always set.\n" +
                "ELEMENT MULTIPLIER m (percent) by attack element against defender element: physical: 100 " +
                "against everything except ghost = 0. fire: fire 50, water 50, nature 200, neutral 100, " +
                "ghost 100. water: fire 200, water 50, nature 50, neutral 100, ghost 100. nature: fire 50, " +
                "water 200, nature 50, neutral 100, ghost 100.\n" +
                "RESOLUTION: keep a running hp and shield (starting at defender.hp and defender.shield) and " +
                "resolve hits k = 1, 2, ..., hits in order; stop as soon as hp is 0 (later hits are not " +
                "resolved). Each hit:\n" +
                "1. Base: p = max(10, power - 20*(k-1)); d = floor(atk * p / 100).\n" +
                "2. Level: diff = attacker.level - defender.level clamped to [-10, 10]; " +
                "d = floor(d * (100 + 3*diff) / 100).\n" +
                "3. Critical: the hit is critical when (seed + 17*k) % 100 < crit_chance; then " +
                "d = floor(d * crit_mult / 100).\n" +
                "4. Armor (skipped when true_damage): pen = pen_pct, doubled on a critical hit, capped at 100; " +
                "ea = floor(armor * (100 - pen) / 100) - pen_flat, but at least 0; " +
                "d = floor(d * 100 / (100 + ea)).\n" +
                "5. Element (skipped when true_damage): d = floor(d * m / 100); r = resist[skill element] " +
                "clamped to [-50, 80]; d = floor(d * (100 - r) / 100).\n" +
                "6. Guard: if guarding, d = floor(d / 2).\n" +
                "7. Minimum: if d < 1 then d = 1, EXCEPT when step 5 ran with m = 0 (immune): d stays 0.\n" +
                "8. Shield: a = min(shield, d); shield = shield - a; loss = d - a. Shatter: if a > 0 and the " +
                "shield is now 0, loss = floor(loss * 125 / 100).\n" +
                "9. HP: if loss >= hp then overkill = loss - hp, hp = 0 and the defender is killed (no further " +
                "hits); otherwise hp = hp - loss.\n" +
                "OUTPUT: return a new table with exactly these fields: damage (defender.hp minus the final hp), " +
                "absorbed (total taken by the shield), hp (final), shield (final), overkill (from the killing " +
                "hit, else 0), crits (resolved hits that were critical, immune hits included), hits (resolved " +
                "hits), killed (boolean). All fields except killed are numbers.\n" +
                "EXAMPLE 1: attacker {atk=40, level=12, crit_chance=30, crit_mult=150, pen_pct=25, " +
                "pen_flat=5}, defender {hp=60, shield=10, armor=40, level=10, element='nature', " +
                "resist={fire=20}, guarding=false}, skill {power=120, element='fire', hits=2, " +
                "true_damage=false, seed=3}. Hit 1: base 48, level 50, critical (roll 20) 75, pen 50 so ea 15 " +
                "and d 65, element 130, resist 104, shield takes 10, shatter makes the loss 117, killed. " +
                "Result {damage=60, absorbed=10, hp=0, shield=0, overkill=57, crits=1, hits=1, killed=true}.\n" +
                "EXAMPLE 2: attacker {atk=90, level=3, crit_chance=50, crit_mult=200, pen_pct=40, " +
                "pen_flat=10}, defender {hp=300, shield=25, armor=60, level=20, element='fire', " +
                "resist={water=-30}, guarding=true}, skill {power=100, element='water', hits=3, " +
                "true_damage=false, seed=40}. Hit losses 50 (shatter), 50, 37; no critical hit. Result " +
                "{damage=137, absorbed=25, hp=163, shield=0, overkill=0, crits=0, hits=3, killed=false}.\n" +
                ExtremeRunRules +
                "The harness calls the slot on many hidden attacks and scores each rule family separately, and " +
                "the slot must return the same result for the same input. Tip: write the function as a local, " +
                "run it on both examples inside the same execute_lua chunk and return the results as a string " +
                "(no assert, no error), then register it with logic_define. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run, 2, 3);
                bool installed = env.Lua.LogicSlots.IsOverridden(Slot);
                g.Add("installed", "resolve_attack override installed", 4, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);

                bool shape = RunFamily(env, ResultTableCases, out string shapeDetail);
                g.Add("result_table", "a plain hit returns the full result table with the right values", 6,
                    shape, true, dimension: BenchmarkDimension.TaskCompletion, detail: shapeDetail);

                // WHY this order: the families that leave optional fields nil run last, because a nil index
                // there raises, and a raising slot is reset, which would also fail every family after it.
                bool all = shape;
                all &= AddFamily(g, env, "base_formula", "single neutral hits: floor(atk * p / 100)", 7,
                    BenchmarkDimension.TaskCompletion, BaseCases);
                all &= AddFamily(g, env, "level_scaling", "level difference clamped to [-10, 10]", 7,
                    BenchmarkDimension.Reasoning, LevelCases);
                all &= AddFamily(g, env, "armor_penetration", "percentage then flat penetration, ea >= 0", 9,
                    BenchmarkDimension.Reasoning, ArmorCases);
                all &= AddFamily(g, env, "elements_resist", "element matrix and resistance clamp", 10,
                    BenchmarkDimension.Reasoning, ElementCases);
                all &= AddFamily(g, env, "critical_hits", "deterministic crit roll, multiplier, doubled pen", 10,
                    BenchmarkDimension.Reasoning, CritCases);
                all &= AddFamily(g, env, "multi_hit_shield", "diminishing hits, shield, shatter, overkill, stop",
                    12, BenchmarkDimension.Reasoning, ShieldCases);
                all &= AddFamily(g, env, "full_pipeline", "every rule interacting on 12 hidden attacks", 14,
                    BenchmarkDimension.Reasoning, FullCases);
                all &= AddFamily(g, env, "edge_cases", "immunity, true damage, guard vs minimum, power floor", 8,
                    BenchmarkDimension.Reasoning, EdgeCases);
                all &= AddFamily(g, env, "optional_fields", "nil optional fields take their defaults", 6,
                    BenchmarkDimension.TaskCompletion, OptionalCases);

                bool deterministic = TryResolve(env, FullCases[0], out AttackOutcome first, out _)
                                     && TryResolve(env, FullCases[0], out AttackOutcome second, out _)
                                     && first.SameAs(second);
                g.Add("deterministic", "the same attack twice gives the same result", 2, deterministic,
                    dimension: BenchmarkDimension.Determinism);

                if (!installed && run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                if (all && deterministic)
                {
                    g.Bonus = 10;
                }

                return g;
            }

            private bool AddFamily(ScenarioGrading g, BenchmarkEnvironment env, string id, string description,
                double weight, BenchmarkDimension dimension, AttackCase[] cases)
            {
                bool passed = RunFamily(env, cases, out string detail);
                g.Add(id, $"{description} ({cases.Length} hidden attacks)", weight, passed,
                    dimension: dimension, detail: detail);
                return passed;
            }

            private bool RunFamily(BenchmarkEnvironment env, AttackCase[] cases, out string detail)
            {
                for (int i = 0; i < cases.Length; i++)
                {
                    AttackOutcome expected = DamagePipelineReference.Resolve(cases[i]);
                    if (!TryResolve(env, cases[i], out AttackOutcome actual, out string error))
                    {
                        detail = $"attack {i + 1} [{cases[i].Spec}]: {error}";
                        return false;
                    }

                    if (!actual.SameAs(expected))
                    {
                        detail = $"attack {i + 1} [{cases[i].Spec}]: expected {expected}, got {actual}";
                        return false;
                    }
                }

                detail = null;
                return true;
            }

            private bool TryResolve(BenchmarkEnvironment env, AttackCase c, out AttackOutcome outcome,
                out string error)
            {
                outcome = default;
                if (!env.Lua.LogicSlots.TryInvoke(Slot, out object result, new LuaValue(c.AttackerTable()),
                        new LuaValue(c.DefenderTable()), new LuaValue(c.SkillTable())))
                {
                    error = env.Lua.LogicSlots.IsOverridden(Slot)
                        ? "the slot returned nothing"
                        : "the slot is not installed or raised an error and was removed";
                    return false;
                }

                if (result is not LuaTable table)
                {
                    error = $"returned {result ?? "nil"} instead of a table";
                    return false;
                }

                if (!AttackOutcome.TryRead(table, out outcome, out string field))
                {
                    error = $"result field '{field}' is missing or has the wrong type";
                    return false;
                }

                error = null;
                return true;
            }

            private static readonly AttackCase[] ResultTableCases =
            {
                AttackCase.Full("atk=30 alv=5 hp=100 ar=0 dlv=5 de=neutral pw=100")
            };

            private static readonly AttackCase[] BaseCases =
            {
                AttackCase.Full("atk=37 alv=20 hp=100000 ar=0 dlv=20 de=neutral pw=135"),
                AttackCase.Full("atk=250 alv=20 hp=100000 ar=0 dlv=20 de=neutral pw=13"),
                AttackCase.Full("atk=13 alv=20 hp=100000 ar=0 dlv=20 de=neutral pw=260"),
                AttackCase.Full("atk=99 alv=20 hp=100000 ar=0 dlv=20 de=neutral pw=101"),
                AttackCase.Full("atk=64 alv=20 hp=100000 ar=0 dlv=20 de=neutral pw=150")
            };

            private static readonly AttackCase[] LevelCases =
            {
                AttackCase.Full("atk=50 alv=14 hp=100000 ar=0 dlv=10 de=neutral pw=100"),
                AttackCase.Full("atk=77 alv=3 hp=100000 ar=0 dlv=10 de=neutral pw=100"),
                AttackCase.Full("atk=40 alv=45 hp=100000 ar=0 dlv=30 de=neutral pw=150"),
                AttackCase.Full("atk=91 alv=5 hp=100000 ar=0 dlv=30 de=neutral pw=100"),
                AttackCase.Full("atk=33 alv=40 hp=100000 ar=0 dlv=30 de=neutral pw=100"),
                AttackCase.Full("atk=100 alv=9 hp=100000 ar=0 dlv=10 de=neutral pw=100")
            };

            private static readonly AttackCase[] ArmorCases =
            {
                AttackCase.Full("atk=100 alv=20 hp=100000 ar=50 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=100 alv=20 pp=25 hp=100000 ar=80 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=100 alv=20 pf=30 hp=100000 ar=80 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=120 alv=20 pp=40 pf=20 hp=100000 ar=90 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=60 alv=20 pp=50 pf=40 hp=100000 ar=30 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=75 alv=20 pp=100 hp=100000 ar=200 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=88 alv=20 pp=10 pf=3 hp=100000 ar=33 dlv=20 de=neutral pw=100")
            };

            private static readonly AttackCase[] ElementCases =
            {
                AttackCase.Full("atk=50 alv=20 hp=100000 ar=0 dlv=20 de=nature pw=100 el=fire"),
                AttackCase.Full("atk=51 alv=20 hp=100000 ar=0 dlv=20 de=water pw=100 el=water"),
                AttackCase.Full("atk=50 alv=20 hp=100000 ar=0 dlv=20 de=fire pw=100 el=nature"),
                AttackCase.Full("atk=50 alv=20 hp=100000 ar=0 dlv=20 de=neutral res=fire:20 pw=100 el=fire"),
                AttackCase.Full("atk=45 alv=20 hp=100000 ar=0 dlv=20 de=fire res=water:-30 pw=100 el=water"),
                AttackCase.Full("atk=70 alv=20 hp=100000 ar=0 dlv=20 de=water res=nature:95 pw=100 el=nature"),
                AttackCase.Full("atk=33 alv=20 hp=100000 ar=0 dlv=20 de=nature res=fire:-70 pw=100 el=fire"),
                AttackCase.Full("atk=40 alv=20 hp=100000 ar=0 dlv=20 de=ghost res=fire:50 pw=100 el=water"),
                AttackCase.Full("atk=80 alv=20 hp=100000 ar=0 dlv=20 de=nature res=physical:25 pw=100")
            };

            private static readonly AttackCase[] CritCases =
            {
                AttackCase.Full("atk=50 alv=20 cc=100 cm=150 hp=100000 ar=0 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=50 alv=20 cc=20 cm=200 hp=100000 ar=0 dlv=20 de=neutral pw=100 sd=3"),
                AttackCase.Full("atk=50 alv=20 cc=21 cm=200 hp=100000 ar=0 dlv=20 de=neutral pw=100 sd=3"),
                AttackCase.Full("atk=100 alv=20 cc=50 cm=150 hp=100000 ar=0 dlv=20 de=neutral pw=100 h=4 sd=10"),
                AttackCase.Full("atk=100 alv=20 cc=100 cm=150 pp=30 hp=100000 ar=100 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=100 alv=20 cc=100 cm=150 pp=70 pf=5 hp=100000 ar=60 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=80 alv=20 cc=13 cm=175 hp=100000 ar=0 dlv=20 de=neutral pw=100 h=2 sd=95")
            };

            private static readonly AttackCase[] ShieldCases =
            {
                AttackCase.Full("atk=100 alv=20 hp=1000 ar=0 dlv=20 de=neutral pw=50 h=4"),
                AttackCase.Full("atk=40 alv=20 hp=500 sh=100 ar=0 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=40 alv=20 hp=500 sh=40 ar=0 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=60 alv=20 hp=300 sh=70 ar=0 dlv=20 de=neutral pw=100 h=3"),
                AttackCase.Full("atk=80 alv=20 hp=150 ar=0 dlv=20 de=neutral pw=100 h=4"),
                AttackCase.Full("atk=100 alv=20 hp=100 sh=10 ar=0 dlv=20 de=neutral pw=100"),
                AttackCase.Full("atk=50 alv=20 hp=50 ar=0 dlv=20 de=neutral pw=100 h=3")
            };

            private static readonly AttackCase[] FullCases =
            {
                AttackCase.Full("atk=55 alv=18 cc=40 cm=175 pp=20 pf=8 hp=400 sh=30 ar=60 dlv=21 de=water " +
                                "res=nature:15,fire:-20 pw=110 el=nature h=3 sd=12"),
                AttackCase.Full("atk=72 alv=30 cc=25 cm=200 pp=35 hp=150 ar=120 dlv=24 de=fire res=water:90 " +
                                "g=1 pw=95 el=water h=2 sd=41"),
                AttackCase.Full("atk=33 alv=7 cc=60 cm=150 pp=10 pf=15 hp=90 sh=45 ar=25 dlv=7 de=neutral " +
                                "pw=140 el=fire h=4 sd=77"),
                AttackCase.Full("atk=120 alv=40 cc=15 cm=250 pp=50 pf=10 hp=220 sh=60 ar=150 dlv=52 de=nature " +
                                "res=fire:-60 g=1 pw=130 el=fire h=3 sd=5"),
                AttackCase.Full("atk=48 alv=22 cc=100 cm=160 pp=45 pf=6 hp=500 sh=12 ar=80 dlv=20 de=ghost " +
                                "res=physical:30 pw=105 h=2 sd=8"),
                AttackCase.Full("atk=64 alv=15 cc=35 cm=180 pf=25 hp=130 sh=20 ar=30 dlv=12 de=water " +
                                "res=water:40 pw=90 el=water h=5 sd=63"),
                AttackCase.Full("atk=90 alv=60 cc=50 cm=150 pp=60 pf=20 hp=180 ar=200 dlv=45 de=fire " +
                                "res=nature:-10 pw=100 el=nature h=2 td=1 sd=19"),
                AttackCase.Full("atk=27 alv=3 cc=80 cm=300 pp=30 pf=2 hp=45 sh=5 ar=10 dlv=9 de=fire " +
                                "res=water:-50 g=1 pw=160 el=water h=3 sd=88"),
                AttackCase.Full("atk=150 alv=33 cc=10 cm=150 pp=15 pf=30 hp=600 sh=100 ar=90 dlv=33 de=neutral " +
                                "res=physical:85 pw=75 h=4 sd=2"),
                AttackCase.Full("atk=81 alv=25 cc=45 cm=220 pp=25 pf=12 hp=95 sh=40 ar=55 dlv=31 de=nature " +
                                "res=fire:35,nature:50 g=1 pw=125 el=fire h=3 sd=29"),
                AttackCase.Full("atk=60 alv=50 cc=20 cm=150 pp=80 hp=250 sh=15 ar=70 dlv=38 de=water " +
                                "res=nature:-35 pw=115 el=nature h=4 sd=93"),
                AttackCase.Full("atk=19 alv=1 hp=70 ar=5 dlv=12 de=fire res=fire:10 pw=200 el=fire h=6")
            };

            private static readonly AttackCase[] EdgeCases =
            {
                AttackCase.Full("atk=80 alv=20 cc=100 hp=1000 sh=5 ar=0 dlv=20 de=ghost pw=100"),
                AttackCase.Full("atk=80 alv=20 hp=1000 ar=500 dlv=20 de=ghost res=physical:80 pw=100 td=1"),
                AttackCase.Full("atk=40 alv=20 hp=1000 ar=100 dlv=20 de=nature res=fire:-50 pw=100 el=fire td=1"),
                AttackCase.Full("atk=1 alv=20 hp=1000 ar=0 dlv=20 de=neutral pw=50"),
                AttackCase.Full("atk=1 alv=20 hp=1000 ar=0 dlv=20 de=neutral g=1 pw=100"),
                AttackCase.Full("atk=75 alv=20 hp=1000 ar=0 dlv=20 de=neutral g=1 pw=100"),
                AttackCase.Full("atk=0 alv=20 hp=1000 ar=0 dlv=20 de=neutral pw=100 h=2"),
                AttackCase.Full("atk=200 alv=20 cc=40 hp=10 sh=3 ar=0 dlv=20 de=ghost pw=100 h=3 sd=1"),
                AttackCase.Full("atk=250 alv=20 hp=1000 ar=0 dlv=20 de=neutral pw=7 h=2")
            };

            private static readonly AttackCase[] OptionalCases =
            {
                AttackCase.Sparse("atk=30 alv=5 hp=100 ar=0 dlv=5 de=neutral pw=100"),
                AttackCase.Sparse("atk=40 alv=5 cc=100 hp=100 ar=0 dlv=5 de=neutral pw=100"),
                AttackCase.Sparse("atk=100 alv=5 hp=1000 ar=100 dlv=5 de=nature pw=100"),
                AttackCase.Sparse("atk=30 alv=5 hp=1000 ar=0 dlv=5 de=fire pw=100 el=water"),
                AttackCase.Sparse("atk=50 alv=5 hp=1000 ar=0 dlv=5 de=neutral res=water:50 pw=100 el=fire"),
                AttackCase.Sparse("atk=50 alv=5 hp=1000 ar=0 dlv=5 de=neutral pw=100 h=2 g=0")
            };
        }

        /// <summary>
        /// One hidden attack for <see cref="ExtremeDamagePipeline"/>, written as a compact spec line
        /// (attacker: atk alv cc cm pp pf; defender: hp sh ar dlv de res g; skill: pw el h td sd). A null
        /// field is left out of the Lua table, so the slot sees it as nil.
        /// </summary>
        internal sealed class AttackCase
        {
            public string Spec;
            public int Atk;
            public int AttackerLevel;
            public int? CritChance;
            public int? CritMult;
            public int? PenPct;
            public int? PenFlat;
            public int Hp;
            public int? Shield;
            public int Armor;
            public int DefenderLevel;
            public string DefenderElement;
            public Dictionary<string, int> Resist;
            public bool? Guarding;
            public int Power;
            public string Element;
            public int? Hits;
            public bool? TrueDamage;
            public int? Seed;

            /// <summary>Every optional field set: unlisted ones take their neutral default explicitly.</summary>
            public static AttackCase Full(string spec)
            {
                AttackCase c = Sparse(spec);
                c.CritChance ??= 0;
                c.CritMult ??= 150;
                c.PenPct ??= 0;
                c.PenFlat ??= 0;
                c.Shield ??= 0;
                c.Resist ??= new Dictionary<string, int>();
                c.Guarding ??= false;
                c.Element ??= "physical";
                c.Hits ??= 1;
                c.TrueDamage ??= false;
                c.Seed ??= 0;
                return c;
            }

            /// <summary>Only the listed fields set; the rest stay nil in Lua.</summary>
            public static AttackCase Sparse(string spec)
            {
                AttackCase c = new() { Spec = spec };
                foreach (string token in spec.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = token.IndexOf('=');
                    string key = token.Substring(0, eq);
                    string value = token.Substring(eq + 1);
                    switch (key)
                    {
                        case "atk": c.Atk = int.Parse(value); break;
                        case "alv": c.AttackerLevel = int.Parse(value); break;
                        case "cc": c.CritChance = int.Parse(value); break;
                        case "cm": c.CritMult = int.Parse(value); break;
                        case "pp": c.PenPct = int.Parse(value); break;
                        case "pf": c.PenFlat = int.Parse(value); break;
                        case "hp": c.Hp = int.Parse(value); break;
                        case "sh": c.Shield = int.Parse(value); break;
                        case "ar": c.Armor = int.Parse(value); break;
                        case "dlv": c.DefenderLevel = int.Parse(value); break;
                        case "de": c.DefenderElement = value; break;
                        case "res":
                            c.Resist = new Dictionary<string, int>();
                            foreach (string entry in value.Split(','))
                            {
                                string[] pair = entry.Split(':');
                                c.Resist[pair[0]] = int.Parse(pair[1]);
                            }

                            break;
                        case "g": c.Guarding = value == "1"; break;
                        case "pw": c.Power = int.Parse(value); break;
                        case "el": c.Element = value; break;
                        case "h": c.Hits = int.Parse(value); break;
                        case "td": c.TrueDamage = value == "1"; break;
                        case "sd": c.Seed = int.Parse(value); break;
                        default: throw new ArgumentException($"unknown attack spec key '{key}' in '{spec}'");
                    }
                }

                return c;
            }

            public LuaTable AttackerTable()
            {
                LuaTable t = new();
                t["atk"] = Atk;
                t["level"] = AttackerLevel;
                SetIf(t, "crit_chance", CritChance);
                SetIf(t, "crit_mult", CritMult);
                SetIf(t, "pen_pct", PenPct);
                SetIf(t, "pen_flat", PenFlat);
                return t;
            }

            public LuaTable DefenderTable()
            {
                LuaTable t = new();
                t["hp"] = Hp;
                SetIf(t, "shield", Shield);
                t["armor"] = Armor;
                t["level"] = DefenderLevel;
                t["element"] = DefenderElement;
                if (Resist != null)
                {
                    LuaTable resist = new();
                    foreach (KeyValuePair<string, int> entry in Resist)
                    {
                        resist[entry.Key] = entry.Value;
                    }

                    t["resist"] = resist;
                }

                if (Guarding.HasValue)
                {
                    t["guarding"] = Guarding.Value;
                }

                return t;
            }

            public LuaTable SkillTable()
            {
                LuaTable t = new();
                t["power"] = Power;
                if (Element != null)
                {
                    t["element"] = Element;
                }

                SetIf(t, "hits", Hits);
                if (TrueDamage.HasValue)
                {
                    t["true_damage"] = TrueDamage.Value;
                }

                SetIf(t, "seed", Seed);
                return t;
            }

            private static void SetIf(LuaTable t, string key, int? value)
            {
                if (value.HasValue)
                {
                    t[key] = value.Value;
                }
            }
        }

        /// <summary>The eight result fields <c>resolve_attack</c> must return.</summary>
        internal struct AttackOutcome
        {
            public long Damage;
            public long Absorbed;
            public long Hp;
            public long Shield;
            public long Overkill;
            public long Crits;
            public long Hits;
            public bool Killed;

            public bool SameAs(AttackOutcome other)
            {
                return Damage == other.Damage && Absorbed == other.Absorbed && Hp == other.Hp
                       && Shield == other.Shield && Overkill == other.Overkill && Crits == other.Crits
                       && Hits == other.Hits && Killed == other.Killed;
            }

            /// <summary>Reads the result table; every numeric field must be a whole number.</summary>
            public static bool TryRead(LuaTable t, out AttackOutcome o, out string badField)
            {
                o = default;
                if (!Whole(t, "damage", out o.Damage))
                {
                    badField = "damage";
                    return false;
                }

                if (!Whole(t, "absorbed", out o.Absorbed))
                {
                    badField = "absorbed";
                    return false;
                }

                if (!Whole(t, "hp", out o.Hp))
                {
                    badField = "hp";
                    return false;
                }

                if (!Whole(t, "shield", out o.Shield))
                {
                    badField = "shield";
                    return false;
                }

                if (!Whole(t, "overkill", out o.Overkill))
                {
                    badField = "overkill";
                    return false;
                }

                if (!Whole(t, "crits", out o.Crits))
                {
                    badField = "crits";
                    return false;
                }

                if (!Whole(t, "hits", out o.Hits))
                {
                    badField = "hits";
                    return false;
                }

                LuaValue killed = t["killed"];
                if (killed.Type != LuaValueType.Boolean)
                {
                    badField = "killed";
                    return false;
                }

                o.Killed = killed.Read<bool>();
                badField = null;
                return true;
            }

            private static bool Whole(LuaTable t, string key, out long value)
            {
                value = 0;
                LuaValue v = t[key];
                if (v.Type != LuaValueType.Number)
                {
                    return false;
                }

                double d = v.Read<double>();
                if (double.IsNaN(d) || double.IsInfinity(d) || Math.Floor(d) != d)
                {
                    return false;
                }

                value = (long)d;
                return true;
            }

            public override string ToString()
            {
                return $"{{damage={Damage}, absorbed={Absorbed}, hp={Hp}, shield={Shield}, overkill={Overkill}, " +
                       $"crits={Crits}, hits={Hits}, killed={(Killed ? "true" : "false")}}}";
            }
        }

        /// <summary>
        /// C# reference for <see cref="ExtremeDamagePipeline"/>, written straight from the goal text. Every
        /// intermediate value is a non-negative integer, so integer division is the goal's floor.
        /// </summary>
        internal static class DamagePipelineReference
        {
            public static AttackOutcome Resolve(AttackCase c)
            {
                int critChance = c.CritChance ?? 0;
                int critMult = c.CritMult ?? 150;
                int penPct = c.PenPct ?? 0;
                int penFlat = c.PenFlat ?? 0;
                long shield = c.Shield ?? 0;
                bool guarding = c.Guarding ?? false;
                string element = c.Element ?? "physical";
                int hits = c.Hits ?? 1;
                bool trueDamage = c.TrueDamage ?? false;
                int seed = c.Seed ?? 0;
                int resist = 0;
                if (c.Resist != null && c.Resist.TryGetValue(element, out int listed))
                {
                    resist = listed;
                }

                int r = Math.Min(Math.Max(resist, -50), 80);
                int m = Multiplier(element, c.DefenderElement);
                int diff = Math.Min(Math.Max(c.AttackerLevel - c.DefenderLevel, -10), 10);
                long hp = c.Hp;
                AttackOutcome o = default;
                for (int k = 1; k <= hits && hp > 0; k++)
                {
                    o.Hits++;
                    long d = (long)c.Atk * Math.Max(10, c.Power - 20 * (k - 1)) / 100;
                    d = d * (100 + 3 * diff) / 100;
                    bool crit = (seed + 17 * k) % 100 < critChance;
                    if (crit)
                    {
                        o.Crits++;
                        d = d * critMult / 100;
                    }

                    bool immune = false;
                    if (!trueDamage)
                    {
                        int pen = Math.Min(100, crit ? penPct * 2 : penPct);
                        long ea = Math.Max(0, (long)c.Armor * (100 - pen) / 100 - penFlat);
                        d = d * 100 / (100 + ea);
                        d = d * m / 100;
                        d = d * (100 - r) / 100;
                        immune = m == 0;
                    }

                    if (guarding)
                    {
                        d /= 2;
                    }

                    if (d < 1 && !immune)
                    {
                        d = 1;
                    }

                    long absorbed = Math.Min(shield, d);
                    shield -= absorbed;
                    o.Absorbed += absorbed;
                    long loss = d - absorbed;
                    if (absorbed > 0 && shield == 0)
                    {
                        loss = loss * 125 / 100;
                    }

                    if (loss >= hp)
                    {
                        o.Overkill = loss - hp;
                        hp = 0;
                        o.Killed = true;
                    }
                    else
                    {
                        hp -= loss;
                    }
                }

                o.Hp = hp;
                o.Shield = shield;
                o.Damage = c.Hp - hp;
                return o;
            }

            private static int Multiplier(string attack, string defender)
            {
                switch (attack)
                {
                    case "physical":
                        return defender == "ghost" ? 0 : 100;
                    case "fire":
                        return defender == "nature" ? 200 : defender == "fire" || defender == "water" ? 50 : 100;
                    case "water":
                        return defender == "fire" ? 200 : defender == "water" || defender == "nature" ? 50 : 100;
                    case "nature":
                        return defender == "water" ? 200 : defender == "nature" || defender == "fire" ? 50 : 100;
                    default:
                        throw new ArgumentException($"unknown attack element '{attack}'");
                }
            }
        }
    }
}
#endif
#endif
