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
    /// G4 — playable game. The model designs a small RULE SYSTEM as several interacting logic slots; the
    /// harness then <b>simulates a real playthrough</b> by driving state through those slots and asserting
    /// the trajectory step-by-step. This is the showcase tier: it proves the game actually works by its
    /// own rules end-to-end, and it is the hardest discriminator — every slot must be mutually consistent
    /// and handle the edge cases, or the simulation diverges and the run fails.
    /// </summary>
    internal static class GamePlaythroughScenariosG4
    {
        public static GameBenchmarkScenario[] All()
        {
            return new GameBenchmarkScenario[]
            {
                new CombatPlaythrough(),
                new ShopPlaythrough(),
                new CraftingChainPlaythrough(),
                new ExtremeAutoBattler()
            };
        }

        private abstract class G4Scenario : GameBenchmarkScenario
        {
            public sealed override string Group => "G4";
            public override int Difficulty => 5;
            public override int TokenBudget => 3000;
            public override double TimeBudgetMs => 45000;
            public override float TimeoutSeconds => 300f;

            protected const string VerificationNote =
                "After defining the slots, do not call the slot names directly as global Lua functions; " +
                "the benchmark harness will invoke the registered logic slots with hidden samples.";

            public override AgentConfig BuildAgent(BenchmarkEnvironment env)
            {
                return new AgentBuilder(RoleId)
                    .WithSystemPrompt(SystemPrompt)
                    .WithTool(env.LuaTool())
                    .WithMaxOutputTokens(MaxOutputTokens)
                    .WithMode(AgentMode.ToolsOnly)
                    .BuildDetached();
            }

            protected static void AddToolHygiene(ScenarioGrading g, BenchmarkEnvironment env, RunObservation run,
                double ranWeight = 5, double cleanWeight = 5)
            {
                g.Add("ran_tool", "issued at least one tool call", ranWeight, run.ToolCalls >= 1,
                    dimension: BenchmarkDimension.ToolCorrectness);
                // WHY: a failed call that never ran Lua (unknown tool, unparsable arguments, policy
                // rejection, tool timeout) must fail this checkpoint too, or G4 would charge it nothing.
                g.AddToolErrorCheckpoint("clean_tool", "no failed tool calls or Lua executions", cleanWeight,
                    run.FailedToolCalls == 0 && env.Lua.FailedExecutions == 0,
                    scoresFailedToolCalls: true, scoresInvalidCommands: false,
                    detail: $"{run.FailedToolCalls} failed calls, {env.Lua.FailedExecutions} failed Lua runs");
            }

            protected static bool Num(BenchmarkEnvironment env, string slot, double expected, params object[] a)
            {
                return env.Lua.TryNumber(slot, out double v, a) && Math.Abs(v - expected) < 1e-6;
            }

            protected static bool Bool(BenchmarkEnvironment env, string slot, bool expected, params object[] a)
            {
                return env.Lua.TryBool(slot, out bool v, a) && v == expected;
            }

            protected static bool Str(BenchmarkEnvironment env, string slot, string expected, params object[] a)
            {
                return env.Lua.TryString(slot, out string v, a)
                       && string.Equals((v ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Turn-based combat: damage + clamped HP + death, then simulate a full fight.</summary>
        private sealed class CombatPlaythrough : G4Scenario
        {
            public override string Id => "g4_combat";
            public override string Name => "Combat playthrough";

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("attack_damage");
                env.Lua.DeclareSlot("apply_damage");
                env.Lua.DeclareSlot("is_dead");
            }

            public override string Goal =>
                "Build a turn-based combat system as three logic slots, installed via execute_lua:\n" +
                "1. 'attack_damage' (args: attacker_power, defender_armor) — returns attacker_power minus " +
                "defender_armor, but always at least 1.\n" +
                "2. 'apply_damage' (args: hp, damage) — returns the new hp after taking damage, never below 0.\n" +
                "3. 'is_dead' (args: hp) — returns true when hp is 0 or less.\n" +
                "Use logic_define('name', function(...) ... end) for each. Make the three rules consistent so " +
                "a fight can be simulated with them.\n" +
                VerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                bool installed = env.Lua.LogicSlots.IsOverridden("attack_damage")
                                 && env.Lua.LogicSlots.IsOverridden("apply_damage")
                                 && env.Lua.LogicSlots.IsOverridden("is_dead");
                g.Add("installed", "all three combat slots installed", 10, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);

                g.Add("unit", "attack_damage(12,4)=8, apply_damage(20,8)=12, is_dead(0)=true, is_dead(5)=false",
                    20, Num(env, "attack_damage", 8, 12.0, 4.0) && Num(env, "apply_damage", 12, 20.0, 8.0)
                                                                && Bool(env, "is_dead", true, 0.0) &&
                                                                Bool(env, "is_dead", false, 5.0),
                    dimension: BenchmarkDimension.TaskCompletion);

                // WHY attack_damage(10,10) and is_dead(-3): the minimum was only probed far below zero, and
                // 'hp == 0' passed is_dead because apply_damage already clamps every fight at 0.
                g.Add("edges", "attack_damage(3,10)=1, (10,10)=1 (min 1), apply_damage(5,100)=0, is_dead(-3)=true",
                    20, Num(env, "attack_damage", 1, 3.0, 10.0) && Num(env, "attack_damage", 1, 10.0, 10.0)
                                                                && Num(env, "apply_damage", 0, 5.0, 100.0)
                                                                && Bool(env, "is_dead", true, -3.0),
                    dimension: BenchmarkDimension.Reasoning);

                // Simulated fight: power 12 vs armor 4 (=> 8 dmg/turn) against 20 HP -> 12, 4, 0 over 3 turns.
                bool trajectoryOk = FightMatches(env, 12, 4, 20, out string fightDetail);
                g.Add("simulated_fight", "a full fight plays out correctly (HP 20→12→4→0 in 3 turns)", 40,
                    trajectoryOk, true, dimension: BenchmarkDimension.Reasoning, detail: fightDetail);

                // WHY: the prompt-visible fight alone could be passed by slots tuned to 12/4/20.
                bool hiddenOk = FightMatches(env, 9, 7, 7, out string hiddenDetail)
                                && FightMatches(env, 2, 9, 3, out hiddenDetail)
                                && FightMatches(env, 15, 0, 30, out hiddenDetail);
                g.Add("hidden_fights", "hidden fights (9 vs 7 at 7 HP, 2 vs 9 at 3 HP, 15 vs 0 at 30 HP)", 20,
                    hiddenOk, dimension: BenchmarkDimension.Reasoning, detail: hiddenDetail);

                if (trajectoryOk && hiddenOk)
                {
                    g.Bonus = 8;
                }

                return g;
            }

            /// <summary>
            /// Drives a fight through the three slots and compares the HP after every turn with
            /// max(1, power - armor) damage per turn, ending exactly when is_dead first reports true.
            /// </summary>
            private static bool FightMatches(BenchmarkEnvironment env, double power, double armor, double startHp,
                out string detail)
            {
                List<double> expected = new();
                double expectedHp = startHp;
                while (expectedHp > 0)
                {
                    expectedHp = Math.Max(0, expectedHp - Math.Max(1, power - armor));
                    expected.Add(expectedHp);
                }

                List<double> trajectory = new();
                double hp = startHp;
                bool diedAtEnd = false;
                for (int i = 0; i < expected.Count + 8 && hp > 0; i++)
                {
                    if (!env.Lua.TryNumber("attack_damage", out double dmg, power, armor)
                        || !env.Lua.TryNumber("apply_damage", out double newHp, hp, dmg))
                    {
                        detail = $"fight {power} vs {armor} at {startHp} HP: a slot call failed on turn {i + 1}";
                        return false;
                    }

                    hp = newHp;
                    trajectory.Add(hp);
                    env.Lua.TryBool("is_dead", out diedAtEnd, hp);
                    if (diedAtEnd)
                    {
                        break;
                    }
                }

                bool ok = diedAtEnd && trajectory.Count == expected.Count;
                for (int i = 0; ok && i < expected.Count; i++)
                {
                    ok = Math.Abs(trajectory[i] - expected[i]) < 1e-6;
                }

                detail = ok
                    ? null
                    : $"fight {power} vs {armor} at {startHp} HP: expected HP [{string.Join(", ", expected)}], " +
                      $"got [{string.Join(", ", trajectory)}]{(diedAtEnd ? "" : " without is_dead")}";
                return ok;
            }
        }

        /// <summary>Shop economy: prices + affordability + purchase, then simulate a buying session.</summary>
        private sealed class ShopPlaythrough : G4Scenario
        {
            public override string Id => "g4_shop";
            public override string Name => "Shop playthrough";

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("item_price");
                env.Lua.DeclareSlot("can_buy");
                env.Lua.DeclareSlot("buy");
            }

            public override string Goal =>
                "Build a shop economy as three logic slots, installed via execute_lua:\n" +
                "1. 'item_price' (arg: item) — returns the price: 'sword'=100, 'potion'=30, 'shield'=75, " +
                "anything else = 0.\n" +
                "2. 'can_buy' (args: gold, price) — returns true when gold is at least price.\n" +
                "3. 'buy' (args: gold, price) — returns the remaining gold after paying price.\n" +
                "Use logic_define for each. Keep them consistent so a shopping session can be simulated.\n" +
                VerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);

                bool installed = env.Lua.LogicSlots.IsOverridden("item_price")
                                 && env.Lua.LogicSlots.IsOverridden("can_buy")
                                 && env.Lua.LogicSlots.IsOverridden("buy");
                g.Add("installed", "all three shop slots installed", 10, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);

                g.Add("prices", "item_price sword=100, potion=30, shield=75, unknown=0", 20,
                    Num(env, "item_price", 100, "sword") && Num(env, "item_price", 30, "potion")
                                                         && Num(env, "item_price", 75, "shield") &&
                                                         Num(env, "item_price", 0, "rock"),
                    dimension: BenchmarkDimension.TaskCompletion);

                // WHY can_buy(75,75): 'gold > price' passed every probe, although "at least price" must allow
                // spending the last coin.
                g.Add("rules", "can_buy(150,30)=true, can_buy(20,75)=false, can_buy(75,75)=true, buy(150,30)=120",
                    20, Bool(env, "can_buy", true, 150.0, 30.0) && Bool(env, "can_buy", false, 20.0, 75.0)
                                                                && Bool(env, "can_buy", true, 75.0, 75.0)
                                                                && Num(env, "buy", 120, 150.0, 30.0),
                    dimension: BenchmarkDimension.TaskCompletion);

                // Simulated session: start 150g. Buy potion(30)->120, sword(100)->20, try shield(75)->rejected.
                bool simOk = true;
                double gold = 150;
                gold = Purchase(env, gold, "potion", ref simOk); // 120
                gold = Purchase(env, gold, "sword", ref simOk); // 20
                double beforeShield = gold;
                gold = Purchase(env, gold, "shield", ref simOk); // rejected -> 20
                bool sessionOk = simOk && Math.Abs(gold - 20) < 1e-6 && Math.Abs(beforeShield - 20) < 1e-6;
                g.Add("simulated_session", "a full shopping run ends with exactly 20 gold (shield rejected)", 40,
                    sessionOk, true, dimension: BenchmarkDimension.Reasoning,
                    detail: sessionOk ? null : $"finalGold={gold}");

                // Hidden session: 205g. Sword -> 105, shield -> 30, potion with exactly 30g -> 0, potion -> rejected.
                bool hiddenSim = true;
                double hiddenGold = 205;
                List<double> spent = new();
                foreach (string item in new[] { "sword", "shield", "potion", "potion" })
                {
                    hiddenGold = Purchase(env, hiddenGold, item, ref hiddenSim);
                    spent.Add(hiddenGold);
                }

                bool hiddenOk = hiddenSim && spent.Count == 4 && Math.Abs(spent[0] - 105) < 1e-6
                                && Math.Abs(spent[1] - 30) < 1e-6 && Math.Abs(spent[2]) < 1e-6
                                && Math.Abs(spent[3]) < 1e-6;
                g.Add("hidden_session", "hidden run from 205 gold spends the last coin, then rejects (105, 30, 0, 0)",
                    20, hiddenOk, dimension: BenchmarkDimension.Reasoning,
                    detail: hiddenOk ? null : $"gold after each purchase: [{string.Join(", ", spent)}]");

                if (sessionOk && hiddenOk)
                {
                    g.Bonus = 8;
                }

                return g;
            }

            private static double Purchase(BenchmarkEnvironment env, double gold, string item, ref bool ok)
            {
                if (!env.Lua.TryNumber("item_price", out double price, item)
                    || !env.Lua.TryBool("can_buy", out bool affordable, gold, price))
                {
                    ok = false;
                    return gold;
                }

                if (!affordable)
                {
                    return gold; // rejected — no purchase
                }

                if (!env.Lua.TryNumber("buy", out double remaining, gold, price))
                {
                    ok = false;
                    return gold;
                }

                return remaining;
            }
        }

        /// <summary>Crafting chain: a recipe resolver, then simulate raw wood → plank → table.</summary>
        private sealed class CraftingChainPlaythrough : G4Scenario
        {
            public override string Id => "g4_crafting_chain";
            public override string Name => "Crafting chain playthrough";

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("craft");
            }

            public override string Goal =>
                "Build a crafting system as a single logic slot named 'craft' (args: a, b). Recipes " +
                "(order-independent): wood+wood=plank, plank+plank=table, wood+stone=axe. Any other pair " +
                "returns the string 'none'. Install it via execute_lua as " +
                "logic_define('craft', function(a, b) ... end) — make it consistent so a multi-step " +
                "crafting chain can be simulated (e.g. wood into planks into a table).\n" +
                VerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run);
                g.Add("installed", "craft slot installed", 10, env.Lua.LogicSlots.IsOverridden("craft"),
                    true, dimension: BenchmarkDimension.IntentSequence);

                g.Add("recipes", "wood+wood=plank, plank+plank=table, wood+stone=axe", 20,
                    Str(env, "craft", "plank", "wood", "wood") && Str(env, "craft", "table", "plank", "plank")
                                                               && Str(env, "craft", "axe", "wood", "stone"),
                    dimension: BenchmarkDimension.TaskCompletion);

                // WHY plank+wood and stone+stone: "any plank makes a table" or "any stone makes an axe"
                // passed the pairs probed before.
                g.Add("reject_and_symmetry", "unknown pairs = none, order-independent (stone+wood=axe)", 20,
                    Str(env, "craft", "none", "wood", "gold") && Str(env, "craft", "none", "table", "table")
                                                              && Str(env, "craft", "none", "plank", "wood")
                                                              && Str(env, "craft", "none", "stone", "stone")
                                                              && Str(env, "craft", "axe", "stone", "wood"),
                    dimension: BenchmarkDimension.Reasoning);

                // Simulated chain: 4 wood -> 2 planks -> 1 table.
                bool ok1 = env.Lua.TryString("craft", out string plankA, "wood", "wood");
                bool ok2 = env.Lua.TryString("craft", out string plankB, "wood", "wood");
                bool chainOk = ok1 && ok2
                                   && string.Equals((plankA ?? "").Trim(), "plank", StringComparison.OrdinalIgnoreCase)
                                   && string.Equals((plankB ?? "").Trim(), "plank", StringComparison.OrdinalIgnoreCase)
                                   && env.Lua.TryString("craft", out string table, plankA, plankB)
                                   && string.Equals((table ?? "").Trim(), "table", StringComparison.OrdinalIgnoreCase);
                g.Add("simulated_chain", "wood→plank→table chain produces a table", 40, chainOk,
                    true, dimension: BenchmarkDimension.Reasoning,
                    detail: chainOk ? null : "chain did not reach 'table'");

                if (chainOk)
                {
                    g.Bonus = 8;
                }

                return g;
            }
        }

        /// <summary>
        /// Extreme tier: a stateful team auto-battler written as four slots (start, step, unit query,
        /// result). The harness plays hidden battles one turn at a time and compares the acting unit, every
        /// unit's hp/shield/poison/stun/cooldown and the result after EVERY turn with
        /// <see cref="BattleReference"/>. Initiative ties, targeting ties, turn-start poison, stun that still
        /// ticks the cooldown, skill cooldowns, shields, heals, cleave and the round-limit draw all interact,
        /// so one wrong rule makes a trajectory diverge; families of battles that each stress a subset of the
        /// rules give partial credit, and long mixed battles separate the strongest models.
        /// </summary>
        private sealed class ExtremeAutoBattler : G4Scenario
        {
            private const int MaxStepsPerBattle = 200;

            public override string Id => "g4_extreme_auto_battler";
            public override string Name => "Extreme: team auto-battler playthrough";
            public override bool IsExtreme => true;
            public override int TokenBudget => 12000;
            public override double TimeBudgetMs => 300000;
            public override float TimeoutSeconds => 480f;
            public override int? MaxToolCallRoundtripsOverride => 40;

            public override string WhatItChecks =>
                "stateful auto-battler (initiative, targeting ties, poison, stun, cooldowns, shields, heal, " +
                "cleave, draw) compared turn by turn on 22 hidden battles";

            public override void Prepare(BenchmarkEnvironment env)
            {
                env.Lua.DeclareSlot("battle_start");
                env.Lua.DeclareSlot("battle_step");
                env.Lua.DeclareSlot("unit_state");
                env.Lua.DeclareSlot("battle_result");
            }

            public override string Goal =>
                "EXTREME TASK. Build a deterministic team auto-battler as FOUR logic slots installed with " +
                "execute_lua (logic_define). The sandbox is Lua 5.2: there is no // operator, no bitwise " +
                "operator and no integer subtype (numbers are doubles); math.floor, table.sort and string " +
                "comparison with < are available. Keep the battle state in Lua (upvalues or globals): the " +
                "harness plays several hidden battles and advances each one turn at a time, comparing every " +
                "turn with the expected trajectory.\n" +
                "SLOTS\n" +
                "1. 'battle_start' (team_a, team_b): each team is a Lua array of 1 to 3 unit tables " +
                "{id, hp, atk, def, spd, skill, cd}. ids are unique strings ('A1'..'A3' in team A, 'B1'..'B3' " +
                "in team B); hp >= 1, atk >= 0, def >= 0, spd >= 0 and cd >= 1 are integers; skill is 'none', " +
                "'poison', 'stun', 'cleave', 'shield' or 'heal'. It discards any previous battle, sets up this " +
                "one and returns true. Every unit starts with max_hp = hp, shield 0, poison 0, stun 0 and " +
                "cooldown 0.\n" +
                "2. 'battle_step' (no arguments): resolves exactly one turn and returns the id of the unit whose " +
                "turn it was. If the battle is already over it changes nothing and returns 'end'.\n" +
                "3. 'unit_state' (id): returns a new table {hp=, shield=, poison=, stun=, cooldown=} with that " +
                "unit's current values. It is also called for dead units; for them only hp = 0 is checked.\n" +
                "4. 'battle_result' (no arguments): 'A' or 'B' once that team has won, 'draw', or 'running'.\n" +
                "ROUNDS: each battle_step call gives the turn to the next unit of the current round's order that " +
                "is still alive; a unit that died earlier in the round is skipped and does not use a call. When " +
                "the current round has no such unit left (and on the first call), a new round starts: all " +
                "LIVING units are ordered by spd (higher first), ties by current hp (higher first), then by id " +
                "(ascending, Lua string <). That order is fixed for the whole round. Round 21 never starts: " +
                "the call that would start it ends the battle as a 'draw' and returns 'end' (until that call, " +
                "battle_result() is still 'running').\n" +
                "TURN of unit U, in this order:\n" +
                "1. Poison: if U.poison > 0 then U.hp = U.hp - U.poison (shields do not block poison) and " +
                "U.poison = U.poison - 1; if now U.hp <= 0, U dies (hp = 0) and the turn ends.\n" +
                "2. Cooldown: if U.cooldown > 0 then U.cooldown = U.cooldown - 1.\n" +
                "3. Stun: if U.stun > 0 then U.stun = U.stun - 1 and the turn ends.\n" +
                "4. Action: if U.skill is not 'none' and U.cooldown == 0, U uses its skill and then sets " +
                "U.cooldown = U.cd; otherwise U makes a basic attack.\n" +
                "COMBAT\n" +
                "- Enemy target: the living enemy with the lowest hp + shield; ties by lower hp, then by id " +
                "ascending.\n" +
                "- A hit by U on T: dmg = max(1, U.atk - T.def). T's shield absorbs first: a = min(T.shield, " +
                "dmg), T.shield = T.shield - a, T.hp = T.hp - (dmg - a). If T.hp <= 0, T dies (hp = 0).\n" +
                "- Basic attack: one hit on the enemy target.\n" +
                "- 'poison': one hit on the enemy target; if it survives, its poison = min(6, poison + 2).\n" +
                "- 'stun': one hit on the enemy target; if it survives, its stun = 1.\n" +
                "- 'cleave': one hit on every living enemy.\n" +
                "- 'shield': no hit; the living ally (U included) with the lowest hp (shield not counted; ties " +
                "by id ascending) gets shield = min(20, shield + 2 * U.atk).\n" +
                "- 'heal': no hit; the living ally (U included) with the largest max_hp - hp (ties by id " +
                "ascending) gets hp = min(max_hp, hp + 2 * U.atk).\n" +
                "END: right after a turn in which the last living unit of a team died, the other team has won: " +
                "battle_result() returns its letter and battle_step() returns 'end' from then on.\n" +
                "EXAMPLE (1 vs 1): A1 {hp=20, atk=6, def=1, spd=5, skill='poison', cd=2}, B1 {hp=18, atk=5, " +
                "def=2, spd=5, skill='stun', cd=3}. Round 1 order: A1, B1 (equal spd, A1 has more hp). Call 1 " +
                "returns 'A1': its poison skill hits B1 for 4 (B1 hp 14, poison 2) and A1's cooldown becomes " +
                "2. Call 2 returns 'B1': poison ticks (hp 12, poison 1), then its stun skill hits A1 for 4 " +
                "(A1 hp 16, stun 1) and B1's cooldown becomes 3. Round 2 order: A1, B1. Call 3 returns 'A1': " +
                "cooldown 2 -> 1, stunned (stun 1 -> 0), no action. Call 4 returns 'B1': poison ticks (hp 11, " +
                "poison 0), cooldown 3 -> 2, basic attack on A1 for 4 (A1 hp 12). Call 5 returns 'A1': " +
                "cooldown 1 -> 0, poison skill again (B1 hp 7, poison 2, A1 cooldown 2).\n" +
                GameMechanicScenariosG2.ExtremeRunRules +
                "Tip: write the functions as locals sharing one state table, replay the example inside the same " +
                "execute_lua chunk and return the values as a string (no assert, no error), then register all " +
                "four with logic_define.\n" + VerificationNote;

            public override ScenarioGrading Grade(BenchmarkEnvironment env, RunObservation run)
            {
                ScenarioGrading g = new();
                AddToolHygiene(g, env, run, 2, 3);
                bool installed = env.Lua.LogicSlots.IsOverridden("battle_start")
                                 && env.Lua.LogicSlots.IsOverridden("battle_step")
                                 && env.Lua.LogicSlots.IsOverridden("unit_state")
                                 && env.Lua.LogicSlots.IsOverridden("battle_result");
                g.Add("installed", "all four battle slots installed", 4, installed, true,
                    dimension: BenchmarkDimension.IntentSequence);

                bool started = StartMatches(env, BasicBattles[0], out string startDetail);
                g.Add("start_and_query", "battle_start returns true and the units start at full hp, idle", 6,
                    started, true, dimension: BenchmarkDimension.TaskCompletion, detail: startDetail);

                List<BattlePlay> plays = new();
                bool all = started;
                all &= AddFamily(g, env, plays, "basic_combat", "basic attacks, targeting and initiative ties", 10,
                    BasicBattles, BattleCheck.Complete);
                all &= AddFamily(g, env, plays, "poison_stun", "turn-start poison, stun, cooldown while stunned",
                    13, PoisonStunBattles, BattleCheck.Complete);
                all &= AddFamily(g, env, plays, "support_skills", "cleave, shield and heal with their caps", 13,
                    SupportBattles, BattleCheck.Complete);
                all &= AddFamily(g, env, plays, "draw_rule", "a stalled battle ends as a draw after round 20", 8,
                    DrawBattles, BattleCheck.Complete);
                all &= AddFamily(g, env, plays, "mixed_battles", "long mixed 3v3 battles match to the last turn",
                    15, MixedBattles, BattleCheck.Complete);
                all &= AddFamily(g, env, null, "mixed_prefix", "mixed 3v3 battles match for their first 12 turns",
                    8, MixedBattles, BattleCheck.Prefix12, plays);

                string initiativeDetail = null;
                string winnerDetail = null;
                foreach (BattlePlay play in plays)
                {
                    initiativeDetail ??= play.FirstRoundDetail;
                    winnerDetail ??= play.WinnerDetail;
                }

                g.Add("initiative_order", $"round-1 turn order of every battle ({plays.Count} hidden battles)", 10,
                    initiativeDetail == null, dimension: BenchmarkDimension.Reasoning, detail: initiativeDetail);
                g.Add("winners", $"final result of every battle ({plays.Count} hidden battles)", 8,
                    winnerDetail == null, dimension: BenchmarkDimension.TaskCompletion, detail: winnerDetail);
                all &= initiativeDetail == null && winnerDetail == null;

                if (!installed && run.ToolCalls == 0)
                {
                    g.HardCap = 40;
                }

                if (all)
                {
                    g.Bonus = 10;
                }

                return g;
            }

            private enum BattleCheck
            {
                Complete,
                Prefix12
            }

            /// <summary>How far one hidden battle matched, and its first mismatch of each kind.</summary>
            private sealed class BattlePlay
            {
                public BattleCase Case;
                public int TraceLength;
                public int MatchedSteps;
                public bool Complete;
                public string Detail;
                public string FirstRoundDetail;
                public string WinnerDetail;
            }

            private bool AddFamily(ScenarioGrading g, BenchmarkEnvironment env, List<BattlePlay> plays, string id,
                string description, double weight, BattleCase[] battles, BattleCheck check,
                List<BattlePlay> alreadyPlayed = null)
            {
                string detail = null;
                for (int i = 0; i < battles.Length; i++)
                {
                    BattlePlay play = alreadyPlayed?.Find(p => ReferenceEquals(p.Case, battles[i]))
                                      ?? Play(env, battles[i]);
                    plays?.Add(play);
                    bool ok = check == BattleCheck.Complete
                        ? play.Complete
                        : play.MatchedSteps >= Math.Min(12, play.TraceLength);
                    if (!ok)
                    {
                        detail ??= $"battle {i + 1} [{battles[i].Spec}]: {play.Detail}";
                    }
                }

                g.Add(id, $"{description} ({battles.Length} hidden battles)", weight, detail == null,
                    dimension: BenchmarkDimension.Reasoning, detail: detail);
                return detail == null;
            }

            private static bool StartMatches(BenchmarkEnvironment env, BattleCase battle, out string detail)
            {
                if (!Bool(env, "battle_start", true, new LuaValue(battle.TeamTable('A')),
                        new LuaValue(battle.TeamTable('B'))))
                {
                    detail = "battle_start did not return true";
                    return false;
                }

                List<BattleStep> trace = BattleReference.Run(battle);
                foreach (KeyValuePair<string, UnitSnapshot> unit in trace[0].Before)
                {
                    if (!UnitMatches(env, unit.Key, unit.Value, out string unitDetail))
                    {
                        detail = $"after battle_start: {unitDetail}";
                        return false;
                    }
                }

                if (!Str(env, "battle_result", "running"))
                {
                    detail = "battle_result() is not 'running' after battle_start";
                    return false;
                }

                detail = null;
                return true;
            }

            /// <summary>
            /// Plays one hidden battle against the reference trajectory. Full state is compared after every
            /// turn until the first mismatch; the model is then still stepped to its end so the round-1 turn
            /// order and the final result can be judged on their own.
            /// </summary>
            private static BattlePlay Play(BenchmarkEnvironment env, BattleCase battle)
            {
                List<BattleStep> trace = BattleReference.Run(battle);
                BattlePlay play = new() { Case = battle, TraceLength = trace.Count };
                string label = $"[{battle.Spec}]";
                // WHY the return value is not checked here: start_and_query already scores 'returns true', and
                // a forgotten 'return true' must not also void every battle the simulation itself gets right.
                if (!env.Lua.LogicSlots.TryInvoke("battle_start", out object _, new LuaValue(battle.TeamTable('A')),
                        new LuaValue(battle.TeamTable('B'))))
                {
                    play.Detail = "battle_start raised an error or is not installed";
                    play.FirstRoundDetail = $"{label}: {play.Detail}";
                    play.WinnerDetail = play.FirstRoundDetail;
                    return play;
                }

                bool diverged = false;
                string lastId = null;
                string stepError = null;
                int calls = 0;
                while (calls < MaxStepsPerBattle && lastId != "end")
                {
                    if (!env.Lua.TryString("battle_step", out string id))
                    {
                        stepError = $"battle_step failed or returned a non-string on call {calls + 1}";
                        play.Detail ??= stepError;
                        break;
                    }

                    calls++;
                    lastId = (id ?? "").Trim();
                    BattleStep expected = calls <= trace.Count ? trace[calls - 1] : null;
                    if (expected != null && expected.Round == 1 && play.FirstRoundDetail == null
                        && lastId != expected.Actor)
                    {
                        play.FirstRoundDetail = $"{label} call {calls}: expected '{expected.Actor}', got '{lastId}'";
                    }

                    if (diverged)
                    {
                        continue;
                    }

                    if (expected == null)
                    {
                        diverged = true;
                        play.Detail = $"call {calls}: the battle should already be over, got '{lastId}'";
                        continue;
                    }

                    string mismatch = StepMismatch(env, expected, lastId);
                    if (mismatch != null)
                    {
                        diverged = true;
                        play.Detail = $"call {calls} (round {expected.Round}): {mismatch}";
                        continue;
                    }

                    play.MatchedSteps++;
                }

                play.Complete = !diverged && play.MatchedSteps == trace.Count;
                if (!play.Complete && play.Detail == null)
                {
                    play.Detail = $"matched {play.MatchedSteps} of {trace.Count} calls";
                }

                string expectedResult = trace[trace.Count - 1].Result;
                if (stepError != null)
                {
                    play.WinnerDetail = $"{label}: {stepError}";
                }
                else if (lastId != "end")
                {
                    play.WinnerDetail = $"{label}: battle_step never returned 'end' within {MaxStepsPerBattle} calls";
                }
                else if (!Str(env, "battle_result", expectedResult))
                {
                    env.Lua.TryString("battle_result", out string got);
                    play.WinnerDetail = $"{label}: expected result '{expectedResult}', got '{got}'";
                }

                return play;
            }

            private static string StepMismatch(BenchmarkEnvironment env, BattleStep expected, string actor)
            {
                if (actor != expected.Actor)
                {
                    return $"expected battle_step() = '{expected.Actor}', got '{actor}'";
                }

                foreach (KeyValuePair<string, UnitSnapshot> unit in expected.After)
                {
                    if (!UnitMatches(env, unit.Key, unit.Value, out string unitDetail))
                    {
                        return $"after '{actor}': {unitDetail}";
                    }
                }

                if (!env.Lua.TryString("battle_result", out string result)
                    || !string.Equals((result ?? "").Trim(), expected.Result, StringComparison.Ordinal))
                {
                    return $"after '{actor}': expected battle_result() = '{expected.Result}', got '{result}'";
                }

                return null;
            }

            private static bool UnitMatches(BenchmarkEnvironment env, string id, UnitSnapshot expected,
                out string detail)
            {
                if (!env.Lua.LogicSlots.TryInvoke("unit_state", out object raw, id) || raw is not LuaTable table)
                {
                    detail = $"unit_state('{id}') did not return a table";
                    return false;
                }

                long hp = Whole(table, "hp");
                if (expected.Hp <= 0)
                {
                    detail = hp == 0 ? null : $"{id} should be dead (hp 0), got hp {Describe(table, "hp")}";
                    return detail == null;
                }

                long shield = Whole(table, "shield");
                long poison = Whole(table, "poison");
                long stun = Whole(table, "stun");
                long cooldown = Whole(table, "cooldown");
                if (hp == expected.Hp && shield == expected.Shield && poison == expected.Poison
                    && stun == expected.Stun && cooldown == expected.Cooldown)
                {
                    detail = null;
                    return true;
                }

                detail = $"{id} expected {expected}, got {{hp={Describe(table, "hp")}, " +
                         $"shield={Describe(table, "shield")}, poison={Describe(table, "poison")}, " +
                         $"stun={Describe(table, "stun")}, cooldown={Describe(table, "cooldown")}}}";
                return false;
            }

            private static long Whole(LuaTable t, string key)
            {
                LuaValue v = t[key];
                if (v.Type != LuaValueType.Number)
                {
                    return long.MinValue;
                }

                double d = v.Read<double>();
                return double.IsNaN(d) || double.IsInfinity(d) || Math.Floor(d) != d ? long.MinValue : (long)d;
            }

            private static string Describe(LuaTable t, string key)
            {
                LuaValue v = t[key];
                return v.Type == LuaValueType.Nil ? "nil" : v.ToString();
            }

            // WHY these battles: generated offline and kept only when, together, each family's set triggers every
            // rule it is named for: spd ties won by the higher hp against id order (round 1 and later), full
            // ties won by id, target ties, the damage minimum and skipped dead units (basic); poison killing at
            // turn start, the poison cap, stun before the target acted and a stunned unit's cooldown still
            // ticking (poison_stun); both caps, both ally-target ties, shields changing the enemy target and a
            // cleave killing two (support); a full 20 rounds (draw). Mixed battles run 28 to 82 turns.
            private static readonly BattleCase[] BasicBattles =
            {
                BattleCase.Of("A1:30,5,3,4,none,2 A2:21,6,1,5,none,2 A3:16,2,0,4,none,3 vs B1:22,9,4,4,none,3 " +
                              "B2:13,3,2,3,none,3"),
                BattleCase.Of("A1:23,9,1,5,none,3 A2:22,3,2,5,none,3 vs B1:23,5,3,5,none,3 B2:19,9,4,5,none,1"),
                BattleCase.Of("A1:19,5,2,4,none,2 A2:14,6,2,4,none,3 vs B1:17,5,3,5,none,3 B2:19,3,2,3,none,3 " +
                              "B3:17,4,3,5,none,3"),
                BattleCase.Of("A1:19,9,0,4,none,1 A2:18,4,3,3,none,3 A3:26,4,1,4,none,3 vs B1:16,9,0,5,none,1 " +
                              "B2:25,4,0,3,none,2")
            };

            private static readonly BattleCase[] PoisonStunBattles =
            {
                BattleCase.Of("A1:21,3,3,4,poison,1 vs B1:31,3,1,3,stun,2 B2:23,7,2,6,none,3 B3:33,6,2,4,poison,2"),
                BattleCase.Of("A1:28,8,3,3,poison,3 A2:22,4,3,5,stun,3 vs B1:21,7,2,3,poison,2 B2:21,7,3,3,poison,3 " +
                              "B3:20,7,2,3,poison,1"),
                BattleCase.Of("A1:33,6,3,3,stun,2 vs B1:18,9,4,4,none,3 B2:17,8,4,4,poison,3 B3:30,3,0,5,poison,1"),
                BattleCase.Of("A1:34,9,0,6,poison,1 A2:27,3,0,6,poison,2 A3:28,9,3,6,stun,3 vs B1:33,7,4,4,poison,2 " +
                              "B2:34,4,3,3,poison,3 B3:20,3,2,5,stun,1"),
                BattleCase.Of("A1:29,5,3,3,poison,3 A2:24,3,1,4,poison,1 vs B1:28,4,3,5,poison,1 B2:33,3,0,3,poison,2 " +
                              "B3:15,5,2,4,stun,1")
            };

            private static readonly BattleCase[] SupportBattles =
            {
                BattleCase.Of("A1:32,5,1,6,cleave,3 A2:20,4,2,6,none,2 A3:19,7,0,3,heal,1 vs B1:21,3,3,3,cleave,1 " +
                              "B2:13,3,2,5,none,3"),
                BattleCase.Of("A1:20,7,2,6,cleave,2 A2:18,9,1,6,shield,2 A3:22,8,3,6,cleave,2 vs B1:23,3,1,4,cleave,2 " +
                              "B2:30,5,3,4,shield,3 B3:18,8,0,4,cleave,2"),
                BattleCase.Of("A1:20,7,0,4,none,2 A2:25,6,3,5,heal,2 A3:19,8,0,4,shield,3 vs B1:23,8,0,4,shield,2 " +
                              "B2:18,8,2,3,shield,3"),
                BattleCase.Of("A1:28,6,0,6,shield,1 A2:27,9,3,5,shield,3 A3:27,7,1,3,cleave,1 vs B1:32,4,0,6,shield,3 " +
                              "B2:26,4,0,5,none,2 B3:31,7,3,5,cleave,3"),
                BattleCase.Of("A1:17,8,0,6,cleave,1 A2:32,6,0,6,shield,1 vs B1:26,3,2,3,shield,2 B2:23,5,3,5,cleave,2 " +
                              "B3:30,8,0,5,heal,3")
            };

            private static readonly BattleCase[] DrawBattles =
            {
                BattleCase.Of("A1:31,3,2,3,none,1 A2:42,4,6,3,none,3 vs B1:37,4,5,5,none,2 B2:50,5,4,5,shield,1"),
                BattleCase.Of("A1:38,5,3,4,shield,2 A2:37,4,2,3,none,1 vs B1:31,3,6,3,heal,1 B2:37,3,6,3,none,3")
            };

            private static readonly BattleCase[] MixedBattles =
            {
                BattleCase.Of("A1:40,3,0,3,poison,2 A2:25,6,1,5,heal,2 A3:29,8,4,6,cleave,3 vs B1:32,4,0,6,stun,2 " +
                              "B2:31,8,4,3,none,1 B3:39,3,3,3,shield,1"),
                BattleCase.Of("A1:22,4,4,5,stun,2 A2:35,8,1,7,stun,1 A3:32,6,0,7,poison,1 vs B1:20,7,0,7,none,3 " +
                              "B2:39,8,3,4,heal,3 B3:22,5,0,6,heal,3"),
                BattleCase.Of("A1:27,9,3,4,none,2 A2:40,9,0,5,shield,2 A3:34,4,1,2,cleave,1 vs B1:21,6,1,2,heal,3 " +
                              "B2:37,8,2,5,poison,3 B3:37,9,2,2,stun,3"),
                BattleCase.Of("A1:29,6,2,7,none,1 A2:24,3,4,6,poison,1 A3:24,9,1,5,poison,2 vs B1:22,9,1,5,shield,1 " +
                              "B2:31,7,0,4,heal,2 B3:27,5,3,2,shield,1"),
                BattleCase.Of("A1:30,7,3,2,cleave,3 A2:25,3,4,5,stun,3 A3:20,9,0,4,shield,3 vs B1:26,9,2,3,cleave,1 " +
                              "B2:28,3,0,7,heal,1 B3:25,6,1,6,stun,3"),
                BattleCase.Of("A1:23,5,0,4,none,3 A2:34,6,0,7,stun,3 A3:18,4,3,4,heal,3 vs B1:25,5,1,5,stun,3 " +
                              "B2:24,9,2,7,none,2 B3:21,5,1,3,stun,3")
            };
        }

        /// <summary>
        /// One hidden battle for <see cref="ExtremeAutoBattler"/>, written as
        /// "A1:hp,atk,def,spd,skill,cd A2:... vs B1:...".
        /// </summary>
        internal sealed class BattleCase
        {
            public string Spec;
            public readonly List<BattleUnitSpec> Units = new();

            public static BattleCase Of(string spec)
            {
                BattleCase battle = new() { Spec = spec };
                foreach (string token in spec.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token == "vs")
                    {
                        continue;
                    }

                    string[] idAndStats = token.Split(':');
                    string[] stats = idAndStats[1].Split(',');
                    battle.Units.Add(new BattleUnitSpec
                    {
                        Id = idAndStats[0],
                        Hp = int.Parse(stats[0]),
                        Atk = int.Parse(stats[1]),
                        Def = int.Parse(stats[2]),
                        Spd = int.Parse(stats[3]),
                        Skill = stats[4],
                        Cd = int.Parse(stats[5])
                    });
                }

                return battle;
            }

            /// <summary>A fresh Lua array of the given team's unit tables; the slot may keep or mutate it.</summary>
            public LuaTable TeamTable(char team)
            {
                LuaTable t = new();
                int index = 1;
                foreach (BattleUnitSpec u in Units)
                {
                    if (u.Id[0] != team)
                    {
                        continue;
                    }

                    LuaTable unit = new();
                    unit["id"] = u.Id;
                    unit["hp"] = u.Hp;
                    unit["atk"] = u.Atk;
                    unit["def"] = u.Def;
                    unit["spd"] = u.Spd;
                    unit["skill"] = u.Skill;
                    unit["cd"] = u.Cd;
                    t[index++] = unit;
                }

                return t;
            }
        }

        internal sealed class BattleUnitSpec
        {
            public string Id;
            public int Hp;
            public int Atk;
            public int Def;
            public int Spd;
            public string Skill;
            public int Cd;
        }

        /// <summary>One unit's observable state; <see cref="Hp"/> 0 means dead.</summary>
        internal struct UnitSnapshot
        {
            public long Hp;
            public long Shield;
            public long Poison;
            public long Stun;
            public long Cooldown;

            public override string ToString()
            {
                return $"{{hp={Hp}, shield={Shield}, poison={Poison}, stun={Stun}, cooldown={Cooldown}}}";
            }
        }

        /// <summary>
        /// One battle_step call of the reference trajectory: what it returned, which round it belonged to,
        /// the result right after it and every unit before and after it.
        /// </summary>
        internal sealed class BattleStep
        {
            public string Actor;
            public int Round;
            public string Result;
            public Dictionary<string, UnitSnapshot> Before;
            public Dictionary<string, UnitSnapshot> After;
        }

        /// <summary>
        /// C# reference for <see cref="ExtremeAutoBattler"/>, written straight from the goal text. Its trace
        /// ends with the first call that returns "end".
        /// </summary>
        internal static class BattleReference
        {
            private const int MaxRounds = 20;

            private sealed class Unit
            {
                public string Id;
                public char Team;
                public long Hp;
                public long MaxHp;
                public long Atk;
                public long Def;
                public long Spd;
                public string Skill;
                public long Cd;
                public long Shield;
                public long Poison;
                public long Stun;
                public long Cooldown;

                public bool Alive => Hp > 0;
            }

            public static List<BattleStep> Run(BattleCase battle)
            {
                List<Unit> units = new();
                foreach (BattleUnitSpec s in battle.Units)
                {
                    units.Add(new Unit
                    {
                        Id = s.Id, Team = s.Id[0], Hp = s.Hp, MaxHp = s.Hp, Atk = s.Atk, Def = s.Def, Spd = s.Spd,
                        Skill = s.Skill, Cd = s.Cd
                    });
                }

                List<BattleStep> trace = new();
                List<Unit> order = new();
                int position = 0;
                int round = 0;
                string result = "running";
                while (true)
                {
                    Dictionary<string, UnitSnapshot> before = Snapshot(units);
                    string actor;
                    if (result != "running")
                    {
                        actor = "end";
                    }
                    else
                    {
                        Unit next = null;
                        while (next == null)
                        {
                            while (position < order.Count && !order[position].Alive)
                            {
                                position++;
                            }

                            if (position < order.Count)
                            {
                                next = order[position++];
                            }
                            else if (round == MaxRounds)
                            {
                                result = "draw";
                                break;
                            }
                            else
                            {
                                round++;
                                order = units.FindAll(u => u.Alive);
                                order.Sort(Initiative);
                                position = 0;
                            }
                        }

                        if (next == null)
                        {
                            actor = "end";
                        }
                        else
                        {
                            TakeTurn(next, units);
                            if (!units.Exists(u => u.Team == 'A' && u.Alive))
                            {
                                result = "B";
                            }
                            else if (!units.Exists(u => u.Team == 'B' && u.Alive))
                            {
                                result = "A";
                            }

                            actor = next.Id;
                        }
                    }

                    trace.Add(new BattleStep
                    {
                        Actor = actor, Round = round, Result = result, Before = before, After = Snapshot(units)
                    });
                    if (actor == "end")
                    {
                        return trace;
                    }
                }
            }

            private static int Initiative(Unit a, Unit b)
            {
                if (a.Spd != b.Spd)
                {
                    return b.Spd.CompareTo(a.Spd);
                }

                if (a.Hp != b.Hp)
                {
                    return b.Hp.CompareTo(a.Hp);
                }

                return string.CompareOrdinal(a.Id, b.Id);
            }

            private static void TakeTurn(Unit u, List<Unit> units)
            {
                if (u.Poison > 0)
                {
                    u.Hp -= u.Poison;
                    u.Poison--;
                    if (u.Hp <= 0)
                    {
                        Die(u);
                        return;
                    }
                }

                if (u.Cooldown > 0)
                {
                    u.Cooldown--;
                }

                if (u.Stun > 0)
                {
                    u.Stun--;
                    return;
                }

                if (u.Skill == "none" || u.Cooldown > 0)
                {
                    Hit(u, EnemyTarget(u, units));
                    return;
                }

                switch (u.Skill)
                {
                    case "poison":
                    {
                        Unit t = EnemyTarget(u, units);
                        Hit(u, t);
                        if (t.Alive)
                        {
                            t.Poison = Math.Min(6, t.Poison + 2);
                        }

                        break;
                    }
                    case "stun":
                    {
                        Unit t = EnemyTarget(u, units);
                        Hit(u, t);
                        if (t.Alive)
                        {
                            t.Stun = 1;
                        }

                        break;
                    }
                    case "cleave":
                        foreach (Unit t in units.FindAll(x => x.Team != u.Team && x.Alive))
                        {
                            Hit(u, t);
                        }

                        break;
                    case "shield":
                    {
                        Unit ally = null;
                        foreach (Unit x in units)
                        {
                            if (x.Team == u.Team && x.Alive
                                                 && (ally == null || x.Hp < ally.Hp
                                                                  || x.Hp == ally.Hp
                                                                  && string.CompareOrdinal(x.Id, ally.Id) < 0))
                            {
                                ally = x;
                            }
                        }

                        ally.Shield = Math.Min(20, ally.Shield + 2 * u.Atk);
                        break;
                    }
                    case "heal":
                    {
                        Unit ally = null;
                        foreach (Unit x in units)
                        {
                            if (x.Team == u.Team && x.Alive
                                                 && (ally == null || x.MaxHp - x.Hp > ally.MaxHp - ally.Hp
                                                                  || x.MaxHp - x.Hp == ally.MaxHp - ally.Hp
                                                                  && string.CompareOrdinal(x.Id, ally.Id) < 0))
                            {
                                ally = x;
                            }
                        }

                        ally.Hp = Math.Min(ally.MaxHp, ally.Hp + 2 * u.Atk);
                        break;
                    }
                    default:
                        throw new ArgumentException($"unknown skill '{u.Skill}'");
                }

                u.Cooldown = u.Cd;
            }

            private static Unit EnemyTarget(Unit u, List<Unit> units)
            {
                Unit best = null;
                foreach (Unit x in units)
                {
                    if (x.Team == u.Team || !x.Alive)
                    {
                        continue;
                    }

                    if (best == null || x.Hp + x.Shield < best.Hp + best.Shield
                                     || x.Hp + x.Shield == best.Hp + best.Shield
                                     && (x.Hp < best.Hp || x.Hp == best.Hp && string.CompareOrdinal(x.Id, best.Id) < 0))
                    {
                        best = x;
                    }
                }

                return best;
            }

            private static void Hit(Unit u, Unit t)
            {
                long dmg = Math.Max(1, u.Atk - t.Def);
                long absorbed = Math.Min(t.Shield, dmg);
                t.Shield -= absorbed;
                t.Hp -= dmg - absorbed;
                if (t.Hp <= 0)
                {
                    Die(t);
                }
            }

            private static void Die(Unit u)
            {
                u.Hp = 0;
                u.Shield = 0;
                u.Poison = 0;
                u.Stun = 0;
            }

            private static Dictionary<string, UnitSnapshot> Snapshot(List<Unit> units)
            {
                Dictionary<string, UnitSnapshot> map = new();
                foreach (Unit u in units)
                {
                    map[u.Id] = new UnitSnapshot
                    {
                        Hp = u.Hp, Shield = u.Shield, Poison = u.Poison, Stun = u.Stun, Cooldown = u.Cooldown
                    };
                }

                return map;
            }
        }
    }
}
#endif
#endif
