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
    /// G3 — reasoning &amp; design. Unlike G1/G2, the prompt does NOT spell out the Lua code: the model
    /// must work out the mechanic itself (piecewise pricing, recursion, two-sided clamping, boolean
    /// composition, constraint satisfaction). Graded deterministically by executing the resulting slot on
    /// <b>derived</b> inputs — those checkpoints are tagged <see cref="BenchmarkDimension.Reasoning"/>, the
    /// axis that separates "follows instructions" (any 4B model) from "is actually smart". These tasks are
    /// still real game mechanics, so a high score also means the model is fit to drive the game.
    /// </summary>
    internal static class GameReasoningScenariosG3
    {
        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new QuadraticScore(),
                new TieredPricing(),
                new ClampedRegen(),
                new FibonacciRewards(),
                new BooleanDungeon(),
                new BalancedEnemies(),
                new ExtremeDungeonRoute()
            };
        }

        // --- bases -----------------------------------------------------------------------------------

        private abstract class G3Scenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G3";
            public override int Difficulty => 4;
            public override int TokenBudget => 2600;
            public override double TimeBudgetMs => 35000;
            public override float TimeoutSeconds => 300f;

            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run,
                double ranWeight = 5, double cleanWeight = 5)
            {
                g.Add("ran_tool", "issued at least one tool call", ranWeight, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                g.AddToolErrorCheckpoint("clean_tool", "no failed tool calls or invalid commands", cleanWeight,
                    run.FailedToolCalls == 0 && env.World.InvalidCommandCount == 0,
                    scoresFailedToolCalls: true, scoresInvalidCommands: true);
            }

            protected static bool SpawnedExactly(BenchmarkEnvironment env, string exactName)
            {
                foreach (RecordedWorldCommand c in env.World.Commands)
                {
                    if (string.Equals(c.Action, "spawn", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.TargetName?.Trim(), exactName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private abstract class LuaG3Scenario : G3Scenario
        {
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

            protected bool Num(BenchmarkEnvironment env, double expected, params object[] args)
            {
                return env.Lua.TryNumber(Slot, out double v, args) && Math.Abs(v - expected) < 1e-6;
            }

            protected bool Bool(BenchmarkEnvironment env, bool expected, params object[] args)
            {
                return env.Lua.TryBool(Slot, out bool v, args) && v == expected;
            }
        }

        // --- scenarios -------------------------------------------------------------------------------

        /// <summary>combo² × 10 — simple non-linear formula the model must derive.</summary>
        private sealed class QuadraticScore : LuaG3Scenario
        {
            public override string Id => "g3_quadratic_score";
            public override string Name => "Quadratic combo score";
            protected override string Slot => "score";

            public override string Goal =>
                "Define a logic slot named 'score' that takes one argument 'combo': the score equals the " +
                "combo squared, multiplied by 10. Install it with the execute_lua tool as " +
                "logic_define('score', function(combo) ... end) — work out the body yourself, do not " +
                "hardcode a table of values. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "score slot installed", 10, env.Lua.LogicSlots.IsOverridden(Slot),
                    true, dimension: BenchmarkDimension.IntentSequence);
                g.Add("stated", "score(1)==10", 10, Num(env, 10, 1.0), dimension: BenchmarkDimension.TaskCompletion);
                g.Add("derived", "score(0,2,3,5,10) == combo²·10", 65,
                    Num(env, 0, 0.0) && Num(env, 40, 2.0) && Num(env, 90, 3.0) && Num(env, 250, 5.0)
                    && Num(env, 1000, 10.0), true, dimension: BenchmarkDimension.Reasoning);

                if (Num(env, 1000, 10.0))
                {
                    g.Bonus = 5;
                }

                return g;
            }
        }

        /// <summary>Piecewise tiered pricing — multi-tier reasoning, no formula given.</summary>
        private sealed class TieredPricing : LuaG3Scenario
        {
            public override string Id => "g3_tiered_pricing";
            public override string Name => "Tiered shop pricing";
            protected override string Slot => "price";

            public override string Goal =>
                "Define a logic slot named 'price' that takes one argument 'qty' for a shop. Pricing tiers: " +
                "the first 10 units cost 5 gold each; units 11 to 20 cost 4 gold each; every unit beyond 20 " +
                "costs 3 gold each. price returns the TOTAL cost of buying qty units. Install it via " +
                "execute_lua as logic_define('price', function(qty) ... end) — derive the piecewise formula " +
                "yourself. " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "price slot installed", 10, env.Lua.LogicSlots.IsOverridden(Slot),
                    true, dimension: BenchmarkDimension.IntentSequence);

                // Within tier 1 — relatively easy.
                g.Add("tier1", "price(5)==25 and price(10)==50", 15,
                    Num(env, 25, 5.0) && Num(env, 50, 10.0), dimension: BenchmarkDimension.TaskCompletion);

                // Cross-tier — needs real piecewise reasoning.
                // WHY 21 and 100: the first unit of the third tier and a large order were never probed.
                bool reason = Num(env, 54, 11.0) // 50 + 1*4
                              && Num(env, 70, 15.0) // 50 + 5*4
                              && Num(env, 90, 20.0) // 50 + 10*4
                              && Num(env, 93, 21.0) // 90 + 1*3
                              && Num(env, 105, 25.0) // 90 + 5*3
                              && Num(env, 120, 30.0) // 90 + 10*3
                              && Num(env, 330, 100.0); // 90 + 80*3
                g.Add("cross_tier", "price(11,15,20,21,25,30,100) follow the tiers", 65, reason,
                    true, dimension: BenchmarkDimension.Reasoning);

                if (reason)
                {
                    g.Bonus = 6;
                }

                return g;
            }
        }

        /// <summary>Two-sided clamp on a 3-arg function — derive the bounds logic.</summary>
        private sealed class ClampedRegen : LuaG3Scenario
        {
            public override string Id => "g3_clamped_regen";
            public override string Name => "Clamped HP regen";
            protected override string Slot => "regen";

            public override string Goal =>
                "Define a logic slot named 'regen' that takes three arguments (hp, amount, max_hp) for a " +
                "healing system: it returns hp + amount, but the result must never exceed max_hp and never " +
                "drop below 0. Install it via execute_lua as " +
                "logic_define('regen', function(hp, amount, max_hp) ... end) — figure out the clamping yourself. " +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "regen slot installed", 10, env.Lua.LogicSlots.IsOverridden(Slot),
                    true, dimension: BenchmarkDimension.IntentSequence);
                g.Add("normal", "regen(50,30,100)==80, regen(5,3,200)==8", 15,
                    Num(env, 80, 50.0, 30.0, 100.0) && Num(env, 8, 5.0, 3.0, 200.0),
                    dimension: BenchmarkDimension.TaskCompletion);

                // WHY other max_hp values: every sample used max_hp = 100, so a hardcoded min(100, ...)
                // passed the whole scenario without reading the third argument.
                bool clampHigh = Num(env, 100, 90.0, 20.0, 100.0) && Num(env, 100, 100.0, 50.0, 100.0)
                                                                 && Num(env, 40, 30.0, 25.0, 40.0)
                                                                 && Num(env, 120, 150.0, 10.0, 120.0);
                bool clampLow = Num(env, 0, 10.0, -30.0, 100.0) && Num(env, 0, 0.0, -5.0, 100.0)
                                                               && Num(env, 0, 20.0, -50.0, 250.0);
                g.Add("clamp_high", "never exceeds max_hp (max_hp 100, 40, 120)", 35, clampHigh, true,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("clamp_low", "never drops below 0", 30, clampLow, true,
                    dimension: BenchmarkDimension.Reasoning);

                if (clampHigh && clampLow)
                {
                    g.Bonus = 6;
                }

                return g;
            }
        }

        /// <summary>n-th Fibonacci — recursion/iteration the model must implement, not transcribe.</summary>
        private sealed class FibonacciRewards : LuaG3Scenario
        {
            public override string Id => "g3_fibonacci_rewards";
            public override string Name => "Fibonacci wave rewards";
            public override int Difficulty => 5;
            protected override string Slot => "wave_reward";

            public override string Goal =>
                "Define a logic slot named 'wave_reward' that takes one argument 'n' and returns the n-th " +
                "Fibonacci number, where wave_reward(1)=1, wave_reward(2)=1, wave_reward(3)=2, " +
                "wave_reward(4)=3, wave_reward(5)=5, and each next value is the sum of the two previous ones. " +
                "It must compute the value for any n — do not hardcode a fixed list. Install it via " +
                "execute_lua as logic_define('wave_reward', function(n) ... end). " + LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "wave_reward slot installed", 10, env.Lua.LogicSlots.IsOverridden(Slot),
                    true, dimension: BenchmarkDimension.IntentSequence);
                g.Add("stated", "wave_reward(1)=1, (5)=5", 10, Num(env, 1, 1.0) && Num(env, 5, 5.0),
                    dimension: BenchmarkDimension.TaskCompletion);

                // WHY 15 and 18: a hardcoded list of the first twelve values passed. 18 stays cheap enough for
                // a naive recursive solution (about 5,200 calls) inside the slot's instruction budget.
                bool derived = Num(env, 8, 6.0) && Num(env, 13, 7.0) && Num(env, 55, 10.0) && Num(env, 144, 12.0)
                               && Num(env, 610, 15.0) && Num(env, 2584, 18.0);
                g.Add("derived", "wave_reward(6,7,10,12,15,18) = 8,13,55,144,610,2584", 60, derived, true,
                    dimension: BenchmarkDimension.Reasoning);

                bool deterministic = env.Lua.TryNumber(Slot, out double a, 10.0)
                                     && env.Lua.TryNumber(Slot, out double b, 10.0) && Math.Abs(a - b) < 1e-6;
                g.Add("deterministic", "same n gives same reward", 10, deterministic,
                    dimension: BenchmarkDimension.Determinism);

                if (derived)
                {
                    g.Bonus = 7;
                }

                return g;
            }
        }

        // --- world + lua reasoning -------------------------------------------------------------------

        private abstract class WorldLuaG3Scenario : G3Scenario
        {
            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.WorldTool())
                    .WithTool(env.LuaTool())
                    .WithMaxOutputTokens(MaxOutputTokens)
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
            }
        }

        /// <summary>Boolean composition with negation across three flags.</summary>
        private sealed class BooleanDungeon : WorldLuaG3Scenario
        {
            public override string Id => "g3_boolean_dungeon";
            public override string Name => "Dungeon win logic";

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("can_win");
            }

            public override string Goal =>
                "Build a tiny dungeon. First, with world_command (action='spawn', prefabKey='Cube'), spawn " +
                "three objects named 'Player', 'Key', 'Door'. Then define a boolean logic slot named " +
                "'can_win' that takes three arguments (has_key, at_door, boss_alive) so the player wins ONLY " +
                "when they have the key AND are at the door AND the boss is NOT alive. Install it via " +
                "execute_lua as logic_define('can_win', function(has_key, at_door, boss_alive) ... end). " +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                bool spawned = SpawnedExactly(env, "Player") && SpawnedExactly(env, "Key")
                                                             && SpawnedExactly(env, "Door");
                g.Add("spawned", "Player, Key, Door spawned", 15, spawned,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("installed", "can_win slot installed", 10, env.Lua.LogicSlots.IsOverridden("can_win"),
                    true, dimension: BenchmarkDimension.IntentSequence);

                g.Add("win_case", "can_win(true,true,false)==true", 20, B(env, true, true, true, false),
                    true, dimension: BenchmarkDimension.TaskCompletion);

                // WHY all seven: with only four probed, (has_key and at_door) ~= boss_alive passed although
                // it lets a player with no key win while the boss is alive.
                bool logic = B(env, false, true, true, true) // boss alive -> false (negation)
                             && B(env, false, true, false, false) // not at door -> false
                             && B(env, false, false, true, false) // no key -> false
                             && B(env, false, false, false, false)
                             && B(env, false, true, false, true)
                             && B(env, false, false, true, true)
                             && B(env, false, false, false, true);
                g.Add("logic", "all seven non-winning combinations are false", 50, logic, true,
                    dimension: BenchmarkDimension.Reasoning);

                if (logic && B(env, true, true, true, false))
                {
                    g.Bonus = 6;
                }

                return g;
            }

            private static bool B(BenchmarkEnvironment env, bool expected, bool hasKey, bool atDoor, bool bossAlive)
            {
                return env.Lua.TryBool("can_win", out bool v, hasKey, atDoor, bossAlive) && v == expected;
            }
        }

        /// <summary>Constraint satisfaction: four distinct in-range HP values summing to exactly 400.</summary>
        private sealed class BalancedEnemies : WorldLuaG3Scenario
        {
            public override string Id => "g3_balanced_enemies";
            public override string Name => "Balanced enemy HP";
            public override int Difficulty => 5;

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("enemy_hp");
            }

            public override string Goal =>
                "Spawn four enemies with world_command (action='spawn', prefabKey='Cube') named 'Enemy1', " +
                "'Enemy2', 'Enemy3', 'Enemy4'. Then define a logic slot named 'enemy_hp' that takes one " +
                "argument 'name' and returns that enemy's HP. The four HP values must all be DIFFERENT, each " +
                "between 50 and 200 inclusive, and add up to EXACTLY 400. Choose values that satisfy every " +
                "constraint. Install it via execute_lua as logic_define('enemy_hp', function(name) ... end). " +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                int spawns = 0;
                for (int i = 1; i <= 4; i++)
                {
                    if (SpawnedExactly(env, $"Enemy{i}"))
                    {
                        spawns++;
                    }
                }

                g.Add("spawned", "Enemy1..Enemy4 spawned", 15, spawns == 4,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("installed", "enemy_hp slot installed", 10, env.Lua.LogicSlots.IsOverridden("enemy_hp"),
                    true, dimension: BenchmarkDimension.IntentSequence);

                List<double> hp = new();
                bool allResolved = true;
                for (int i = 1; i <= 4; i++)
                {
                    if (env.Lua.TryNumber("enemy_hp", out double v, $"Enemy{i}"))
                    {
                        hp.Add(v);
                    }
                    else
                    {
                        allResolved = false;
                    }
                }

                bool inRange = allResolved && hp.TrueForAll(v => v >= 50 && v <= 200);
                bool distinct = allResolved && new HashSet<double>(hp).Count == hp.Count;
                double sum = 0;
                foreach (double v in hp)
                {
                    sum += v;
                }

                bool sums = allResolved && Math.Abs(sum - 400) < 1e-6;

                g.Add("resolved", "enemy_hp returns a value for each enemy", 15, allResolved,
                    dimension: BenchmarkDimension.TaskCompletion);
                g.Add("in_range", "every HP within 50..200", 15, inRange,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("distinct", "all four HP values are different", 15, distinct,
                    dimension: BenchmarkDimension.Reasoning);
                g.Add("sum_400", "HP values sum to exactly 400", 15, sums, true,
                    dimension: BenchmarkDimension.Reasoning,
                    detail: allResolved ? $"sum={sum}; HP=[{string.Join(", ", hp)}]" : "unresolved");

                if (spawns == 4 && inRange && distinct && sums)
                {
                    g.Bonus = 7;
                }

                return g;
            }
        }

        /// <summary>
        /// Extreme tier: a route planner over a dungeon grid with swamp costs, key/door pairs, one-way cells
        /// and one-way teleporters, returning the minimum cost and, among the cheapest routes, the fewest
        /// actions. The answer needs a real search over (cell, keys held) — breadth-first by steps, a
        /// per-cell visited set or greedy key collection all fail some hidden grid. Graded on 46 hidden grids
        /// grouped by the rule family they stress; expected answers come from <see cref="RouteReference"/>.
        /// </summary>
        private sealed class ExtremeDungeonRoute : LuaG3Scenario
        {
            public override string Id => "g3_extreme_dungeon_route";
            public override string Name => "Extreme: key-door teleporter route planner";
            public override bool IsExtreme => true;
            public override int Difficulty => 5;
            public override int TokenBudget => 9000;
            public override double TimeBudgetMs => 240000;
            public override float TimeoutSeconds => 480f;
            public override int? MaxToolCallRoundtripsOverride => 40;
            protected override string Slot => "route";

            public override string WhatItChecks =>
                "min-cost route with swamps, key/door state, one-way cells and teleporters plus a fewest-actions " +
                "tie-break, on 46 hidden grids graded per rule family";

            public override string Goal =>
                "EXTREME TASK. Define a logic slot named 'route' that plans the cheapest route through a dungeon " +
                "grid. Install it with execute_lua as logic_define('route', function(grid, teleporters) ... end). " +
                "The sandbox is Lua 5.2: there is no // operator, no bitwise operator and no integer subtype " +
                "(numbers are doubles); string.sub, string.byte, table.insert/remove and math.floor are " +
                "available.\n" +
                "GRID: grid is a Lua array of strings; row 1 is the top row, all rows have the same length, at " +
                "most 6 rows and 6 columns. Cell (r, c) is character c of row r. Characters: 'S' start " +
                "(exactly one), 'G' goal (exactly one), '.' floor, '~' swamp, '#' wall, 'a' and 'b' keys, 'A' " +
                "and 'B' doors, and the one-way cells '>', '<', '^' and 'v'.\n" +
                "TELEPORTERS: teleporters is a Lua array (possibly empty) of tables {fr=, fc=, tr=, tc=, cost=}: " +
                "a ONE-WAY teleporter from cell (fr, fc) to cell (tr, tc). Rows and columns are 1-based and " +
                "always inside the grid; cost is an integer >= 0; several teleporters may share a source.\n" +
                "RULES: the explorer starts on S holding no keys. One action is either\n" +
                "- a move to the cell directly up, down, left or right (inside the grid). Entering a cell costs " +
                "3 for a swamp '~' and 1 for every other enterable cell ('.', 'S', 'G', keys, doors and one-way " +
                "cells). A wall '#' can never be entered; door 'A' can be entered only while holding key 'a', " +
                "door 'B' only while holding key 'b'; a one-way cell can be entered by a move only in its " +
                "direction ('>' only by moving right, '<' only moving left, '^' only moving up, 'v' only " +
                "moving down), while leaving it is unrestricted; or\n" +
                "- a teleport: while standing on a teleporter's source cell, jump to its destination paying " +
                "exactly its cost (the destination's entry cost is NOT added). Allowed only if the destination " +
                "is not a wall and, for a door, its key is held; a teleport may land on a one-way cell whatever " +
                "its direction.\n" +
                "Entering a key cell, by a move or a teleport, picks that key up. Keys are never lost or used " +
                "up, and doors never close. Any cell may be visited any number of times. The route ends the " +
                "moment the explorer stands on G.\n" +
                "OUTPUT: return a new table {cost = C, actions = N}: C is the minimum total cost of a route " +
                "from S to G, and N is the smallest number of actions (moves plus teleports) among the routes " +
                "whose cost is exactly C. If G cannot be reached, return {cost = -1, actions = -1}.\n" +
                "LIMITS: One slot call may run at most 200,000 Lua VM instructions; a call that exceeds it or " +
                "raises uninstalls the slot. Search the states (row, column, keys held), for example Dijkstra " +
                "ordered by cost and then actions: 36 cells x 4 key sets is small, so even picking the next " +
                "state by scanning every state fits easily, but never enumerate whole routes.\n" +
                "EXAMPLE 1: grid {'S~G', '...'}, teleporters {} -> {cost=4, actions=2} (S, swamp, G costs 3+1; " +
                "the four-move detour through row 2 also costs 4 but needs more actions).\n" +
                "EXAMPLE 2: grid {'S.A.G', '#.###', 'a....'}, teleporters {} -> {cost=10, actions=10} (fetch " +
                "key a in the bottom-left corner, come back through (1,2) and open door A).\n" +
                "EXAMPLE 3: grid {'S#G', '.#.', '...'}, teleporters {{fr=1, fc=1, tr=1, tc=3, cost=5}} -> " +
                "{cost=5, actions=1}; with cost=7 instead the six-move walk wins: {cost=6, actions=6}.\n" +
                "EXAMPLE 4: grid {'S<G', '...'}, teleporters {} -> {cost=4, actions=4} (the '<' cell cannot be " +
                "entered moving right or up, so the route goes through row 2).\n" +
                GameMechanicScenariosG2.ExtremeRunRules +
                "The harness calls the slot on many hidden grids and scores each rule family separately. Tip: " +
                "write the function as a local, run it on the examples inside the same execute_lua chunk and " +
                "return the results as a string (no assert, no error), then register it with logic_define. " +
                LuaVerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run, 2, 3);
                bool installed = env.Lua.LogicSlots.IsOverridden(Slot);
                g.Add("installed", "route slot installed", 5, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);

                bool shape = Solve(env, ResultTableCase, out RouteOutcome plain, out string shapeError)
                             && plain.Cost == 2 && plain.Actions == 2;
                g.Add("result_table", "route({'S.G'}, {}) returns {cost=2, actions=2}", 5, shape, true,
                    dimension: BenchmarkDimension.TaskCompletion,
                    detail: shape ? null : shapeError ?? $"got {plain}");

                // WHY one slot call per grid: a cost family and the fewest-actions checkpoint judge the same
                // answer, so cost and actions are read from a single result.
                RouteFamily[] families =
                {
                    new("terrain_only", "swamp costs beat step counts", 10, TerrainCases),
                    new("keys_doors", "keys picked up, doors need them, states revisited", 15, KeyCases),
                    new("teleporters", "one-way teleporters pay only their own cost", 15, TeleporterCases),
                    new("combined", "teleports onto keys and doors, keys before teleports", 15, CombinedCases),
                    new("one_way_cells", "one-way cells entered only along their arrow, teleports land freely", 10,
                        OneWayCases),
                    new("unreachable", "unreachable goals return -1", 10, UnreachableCases)
                };

                bool all = shape;
                string actionsDetail = null;
                int samples = 0;
                foreach (RouteFamily family in families)
                {
                    string costDetail = null;
                    for (int i = 0; i < family.Cases.Length; i++)
                    {
                        RouteCase c = family.Cases[i];
                        RouteOutcome expected = RouteReference.Solve(c.Rows, c.Teleporters);
                        samples++;
                        if (!Solve(env, c, out RouteOutcome actual, out string error))
                        {
                            costDetail ??= $"grid {i + 1} {c}: {error}";
                            actionsDetail ??= $"{family.Id} grid {i + 1} {c}: {error}";
                            continue;
                        }

                        if (actual.Cost != expected.Cost)
                        {
                            costDetail ??= $"grid {i + 1} {c}: expected cost {expected.Cost}, got {actual.Cost}";
                        }

                        if (actual.Actions != expected.Actions)
                        {
                            actionsDetail ??= $"{family.Id} grid {i + 1} {c}: expected actions " +
                                              $"{expected.Actions}, got {actual.Actions}";
                        }
                    }

                    bool passed = costDetail == null;
                    all &= passed;
                    g.Add(family.Id, $"{family.Description} ({family.Cases.Length} hidden grids, cost)",
                        family.Weight, passed, dimension: BenchmarkDimension.Reasoning, detail: costDetail);
                }

                bool fewest = actionsDetail == null;
                g.Add("fewest_actions", $"fewest actions among the cheapest routes ({samples} hidden grids)", 15,
                    fewest, dimension: BenchmarkDimension.Reasoning, detail: actionsDetail);

                if (!installed && run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                if (all && fewest)
                {
                    g.Bonus = 10;
                }

                return g;
            }

            private bool Solve(BenchmarkEnvironment env, RouteCase c, out RouteOutcome outcome, out string error)
            {
                outcome = new RouteOutcome { Cost = long.MinValue, Actions = long.MinValue };
                if (!env.Lua.LogicSlots.TryInvoke(Slot, out object result, new LuaValue(c.GridTable()),
                        new LuaValue(c.TeleporterTable())))
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

                if (!Whole(table, "cost", out outcome.Cost) || !Whole(table, "actions", out outcome.Actions))
                {
                    error = "cost or actions is missing or not a whole number";
                    return false;
                }

                error = null;
                return true;
            }

            private static bool Whole(LuaTable t, string key, out long value)
            {
                value = long.MinValue;
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

            private sealed class RouteFamily
            {
                public readonly string Id;
                public readonly string Description;
                public readonly double Weight;
                public readonly RouteCase[] Cases;

                public RouteFamily(string id, string description, double weight, RouteCase[] cases)
                {
                    Id = id;
                    Description = description;
                    Weight = weight;
                    Cases = cases;
                }
            }

            private static readonly RouteCase ResultTableCase = RouteCase.Of("", "S.G");

            // WHY these grids: generated offline and kept only when the family's typical bug changes the
            // answer (step-count search for terrain, per-cell state or ignored doors for keys, a charged
            // destination entry or no teleports for teleporters, door/pickup rules skipped on teleport for
            // combined, arrows ignored or blocking teleport landings for one-way cells), and the first three
            // of most families also when a cost-only search can return a different action count. The last
            // grid of some families is hand-made for one edge rule.
            private static readonly RouteCase[] TerrainCases =
            {
                RouteCase.Of("", "~...#", "..~.#", "..#G.", "S~~~#", "#....", "#~#~~"),
                RouteCase.Of("", ".....S", "~...##", "...~#G", "...~#.", "~~..~.", "#~~..."),
                RouteCase.Of("", "....", "..~.", ".#~S", "~~~#", "G~#.", "~#~."),
                RouteCase.Of("", "##~~.#", "...~..", "#G~##.", ".~#...", "..~~~S"),
                RouteCase.Of("", "....S", ".#~#~", "#..~~", "~#..#", "~.~G."),
                RouteCase.Of("", ".~...#", ".~S#..", ".~~.~G", "~.#...", "##..#~", ".#...."),
                RouteCase.Of("", "S..~~G")
            };

            private static readonly RouteCase[] KeyCases =
            {
                RouteCase.Of("", "#G#.", ".A#.", ".~.B", ".#.S", ".b.a"),
                RouteCase.Of("", ".b~B.", "..#A.", ".~A#.", ".S#.#", "#BG.a"),
                RouteCase.Of("", ".#..A.", ".#.#.G", "#~..##", "S..~.~", "~A~#.#", "....~a"),
                RouteCase.Of("", "#Aa#~.", ".#.bB~", "B.AS~.", ".~G#.#", "..##~."),
                RouteCase.Of("", ".~B.", "..B.", ".~..", "a~#b", "ASA.", "##.G"),
                RouteCase.Of("", "#.#..G", ".a.#A.", ".~AS.#", "#....."),
                RouteCase.Of("", "...a#", "#..S.", "...A~", "..AG.", "....#")
            };

            private static readonly RouteCase[] TeleporterCases =
            {
                RouteCase.Of("4,5>1,4:1 4,5>1,1:1 2,3>4,2:4", "#....", "#.~~S", ".#.#.", "G.~#."),
                RouteCase.Of("3,3>3,1:2 4,4>2,3:0", "###.", "##..", "G..~", "..#S"),
                RouteCase.Of("2,3>1,4:1 4,1>4,4:4 2,3>3,4:0", "S#..", "....", "~.~#", "..#G"),
                RouteCase.Of("4,2>4,6:4 5,3>4,6:3", "##~~#~", "#~#.#~", "#.~..G", "......", "##~~..", "S.~.~."),
                RouteCase.Of("1,2>4,3:4", "S.~.#", "..#~#", "##...", ".~...", "~.#.~", "G...."),
                RouteCase.Of("3,5>2,3:3 4,3>1,5:0 4,1>3,1:0", "~~...", "....~", "G....", "S~~~#"),
                RouteCase.Of("3,5>1,3:4 3,5>1,2:0 2,1>1,5:0", "..#G~", ".....", ".##.~", "S#.~#"),
                RouteCase.Of("1,1>3,1:0 3,1>1,1:0 1,3>3,5:6 1,3>3,3:0", "S..#G", "##.#.", "....."),
                RouteCase.Of("1,1>3,2:5 3,2>1,6:0", "S....G", "######", "#.####")
            };

            private static readonly RouteCase[] CombinedCases =
            {
                RouteCase.Of("4,1>1,4:4 4,5>3,5:4 2,2>1,5:2", "B.#GA", "..##.", "a..~b", "A..S."),
                RouteCase.Of("1,4>4,1:1 2,4>3,3:3 3,1>3,4:3", "b.S~", "#.#a", "~A.#", "BG.#"),
                RouteCase.Of("4,3>1,5:3 2,5>2,1:0 2,2>4,2:0", "#~~.B~", "BA#..S", ".#~A#a", "G...~b"),
                RouteCase.Of("3,6>2,1:0 5,3>4,2:3 2,5>5,5:4", "#..#b.", "~.~.a~", "~G.#~.", "~BB.A.", ".A.S.."),
                RouteCase.Of("1,1>6,1:3 6,3>2,2:1", "Bb~S", "#.#A", ".#..", ".AG~", "..#.", "a.B."),
                RouteCase.Of("1,1>3,4:2 6,3>1,1:2 5,2>2,4:0", ".Ga#", ".B.A", "A#..", "b#~B", "S.~.", "#..."),
                RouteCase.Of("3,5>2,2:1 1,5>1,2:0", ".~..A", "aB..#", "..#.S", "..G#b")
            };

            private static readonly RouteCase[] OneWayCases =
            {
                RouteCase.Of("", "#~~G<~", ".>.<~.", "~a..#.", "#~.~#.", "#A#S..", ".<.#.~"),
                RouteCase.Of("6,5>3,3:2", ".S.~<.", "a...v.", "~#....", ".^~.v.", "#.A.~.", "#G~..."),
                RouteCase.Of("1,5>4,5:1", "G>...", ".~v..", "#~#..", "..~..", "...S."),
                RouteCase.Of("", "v...", ".~a.", "~S#^", ".~.A", "..^G", "..#."),
                RouteCase.Of("4,1>5,3:4", "S..A", "<~..", "Gv~v", "A~v.", "~.a<"),
                RouteCase.Of("1,5>2,6:2", "va.~.^", "#~...<", "S..##G", "~.~~A."),
                RouteCase.Of("3,3>5,6:1 4,4>4,1:2", ".~^..~", ".S~.##", "......", "^##...", "G####>")
            };

            private static readonly RouteCase[] UnreachableCases =
            {
                RouteCase.Of("", ".S#^", "#^..", "v..<", "...v", "..G."),
                RouteCase.Of("", "###.~", "~#.~.", "aA.#.", "#GBS#", "#..#b"),
                RouteCase.Of("", "#G.#~", "..a..", "#.#..", "....A", "#.##S", "####."),
                RouteCase.Of("6,4>1,1:1 4,3>5,1:4 6,1>2,1:2", "#~~a~", "AG.#~", "....#", "##...", ".A#.#", "S.#.."),
                RouteCase.Of("4,4>1,4:1 4,1>3,1:4 4,2>1,1:0", "b.aA", "~.#G", ".A.#", "..BS"),
                RouteCase.Of("3,1>2,4:2", "~#.#G.", ".~S.##", "...#..", "#..~.#"),
                RouteCase.Of("5,5>6,4:0", "..S.~", "#....", ".#~##", "#.#G.", "~#..~", "~~~.#"),
                RouteCase.Of("", "S..", "###", "..G")
            };
        }

        /// <summary>
        /// One hidden grid for <see cref="ExtremeDungeonRoute"/>. Teleporters are written as
        /// "fr,fc&gt;tr,tc:cost" entries separated by spaces (1-based, as the slot receives them).
        /// </summary>
        internal sealed class RouteCase
        {
            public string[] Rows;
            public int[][] Teleporters;

            public static RouteCase Of(string teleporters, params string[] rows)
            {
                List<int[]> parsed = new();
                foreach (string entry in teleporters.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] ends = entry.Split('>');
                    string[] from = ends[0].Split(',');
                    string[] toAndCost = ends[1].Split(':');
                    string[] to = toAndCost[0].Split(',');
                    parsed.Add(new[]
                    {
                        int.Parse(from[0]), int.Parse(from[1]), int.Parse(to[0]), int.Parse(to[1]),
                        int.Parse(toAndCost[1])
                    });
                }

                return new RouteCase { Rows = rows, Teleporters = parsed.ToArray() };
            }

            public LuaTable GridTable()
            {
                LuaTable t = new();
                for (int i = 0; i < Rows.Length; i++)
                {
                    t[i + 1] = Rows[i];
                }

                return t;
            }

            public LuaTable TeleporterTable()
            {
                LuaTable t = new();
                for (int i = 0; i < Teleporters.Length; i++)
                {
                    int[] tp = Teleporters[i];
                    LuaTable entry = new();
                    entry["fr"] = tp[0];
                    entry["fc"] = tp[1];
                    entry["tr"] = tp[2];
                    entry["tc"] = tp[3];
                    entry["cost"] = tp[4];
                    t[i + 1] = entry;
                }

                return t;
            }

            public override string ToString()
            {
                List<string> tele = new();
                foreach (int[] tp in Teleporters)
                {
                    tele.Add($"{tp[0]},{tp[1]}>{tp[2]},{tp[3]}:{tp[4]}");
                }

                return $"[{string.Join("/", Rows)}{(tele.Count > 0 ? " | " + string.Join(" ", tele) : "")}]";
            }
        }

        /// <summary>Minimum cost and, among routes of that cost, the fewest actions; -1/-1 when unreachable.</summary>
        internal struct RouteOutcome
        {
            public long Cost;
            public long Actions;

            public override string ToString()
            {
                return $"{{cost={Cost}, actions={Actions}}}";
            }
        }

        /// <summary>
        /// C# reference for <see cref="ExtremeDungeonRoute"/>: Dijkstra over (row, column, keys held) with
        /// (cost, actions) compared lexicographically. Both parts only grow along an edge, so the first time
        /// a goal state is settled its pair is the answer.
        /// </summary>
        internal static class RouteReference
        {
            public static RouteOutcome Solve(string[] rows, int[][] teleporters)
            {
                int height = rows.Length;
                int width = rows[0].Length;
                int states = height * width * 4;
                long[] cost = new long[states];
                long[] actions = new long[states];
                bool[] settled = new bool[states];
                for (int i = 0; i < states; i++)
                {
                    cost[i] = long.MaxValue;
                }

                for (int r = 0; r < height; r++)
                {
                    int c = rows[r].IndexOf('S');
                    if (c >= 0)
                    {
                        cost[(r * width + c) * 4] = 0;
                    }
                }

                // WHY this order (up, down, left, right): ArrowDirection returns these move indexes.
                int[] dr = { -1, 1, 0, 0 };
                int[] dc = { 0, 0, -1, 1 };
                while (true)
                {
                    int best = -1;
                    for (int i = 0; i < states; i++)
                    {
                        if (!settled[i] && cost[i] != long.MaxValue
                                        && (best < 0 || cost[i] < cost[best]
                                                     || cost[i] == cost[best] && actions[i] < actions[best]))
                        {
                            best = i;
                        }
                    }

                    if (best < 0)
                    {
                        return new RouteOutcome { Cost = -1, Actions = -1 };
                    }

                    settled[best] = true;
                    int mask = best % 4;
                    int cell = best / 4;
                    int row = cell / width;
                    int col = cell % width;
                    if (rows[row][col] == 'G')
                    {
                        return new RouteOutcome { Cost = cost[best], Actions = actions[best] };
                    }

                    for (int d = 0; d < 4; d++)
                    {
                        int nr = row + dr[d];
                        int nc = col + dc[d];
                        if (nr >= 0 && nr < height && nc >= 0 && nc < width)
                        {
                            Relax(rows, width, cost, actions, best, nr, nc, mask, rows[nr][nc] == '~' ? 3 : 1, d);
                        }
                    }

                    foreach (int[] tp in teleporters)
                    {
                        if (tp[0] - 1 == row && tp[1] - 1 == col)
                        {
                            Relax(rows, width, cost, actions, best, tp[2] - 1, tp[3] - 1, mask, tp[4], -1);
                        }
                    }
                }
            }

            /// <summary>The move index an arrow cell must be entered with, or -1 for any other cell.</summary>
            private static int ArrowDirection(char cell)
            {
                switch (cell)
                {
                    case '^': return 0;
                    case 'v': return 1;
                    case '<': return 2;
                    case '>': return 3;
                    default: return -1;
                }
            }

            /// <param name="move">Move index 0..3, or -1 for a teleport, which may land on an arrow from anywhere.</param>
            private static void Relax(string[] rows, int width, long[] cost, long[] actions, int from, int row,
                int col, int mask, long step, int move)
            {
                char cell = rows[row][col];
                if (cell == '#' || cell == 'A' && (mask & 1) == 0 || cell == 'B' && (mask & 2) == 0)
                {
                    return;
                }

                int arrow = ArrowDirection(cell);
                if (arrow >= 0 && move >= 0 && move != arrow)
                {
                    return;
                }

                int nextMask = mask | (cell == 'a' ? 1 : 0) | (cell == 'b' ? 2 : 0);
                int to = (row * width + col) * 4 + nextMask;
                long nextCost = cost[from] + step;
                long nextActions = actions[from] + 1;
                if (nextCost < cost[to] || nextCost == cost[to] && nextActions < actions[to])
                {
                    cost[to] = nextCost;
                    actions[to] = nextActions;
                }
            }
        }
    }
}
#endif
#endif
