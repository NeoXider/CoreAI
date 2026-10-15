#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using CoreAI.Benchmarking;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// Grader tests for the G1/G7 extreme scenarios and the hardened regular G1/G7 graders. Every golden
    /// world is computed here by its own formulas (iterated rotations, chord interpolation, polar mirroring,
    /// hand-derived key rooms), independently of the graders' formulas, and every golden Lua was written by
    /// hand against the brief.
    /// </summary>
    public sealed class ExtremeWorldScenariosPlayModeTests
    {
        private const string FortressId = "g1_extreme_fortress";
        private const string DungeonId = "g7_extreme_dungeon";

        private const string FortressLua = @"
logic_define('nearest_tower', function(name)
  local p = coreai_world_pos(name)
  if p == nil then return 'none' end
  local dists = {}
  local best = nil
  for k = 1, 8 do
    local t = coreai_world_pos('Tower' .. k)
    if t ~= nil then
      local d = math.sqrt((p.x - t.x) ^ 2 + (p.z - t.z) ^ 2)
      dists[k] = d
      if best == nil or d < best then best = d end
    end
  end
  if best == nil then return 'none' end
  for k = 1, 8 do
    if dists[k] ~= nil and dists[k] <= best * 1.01 then return 'Tower' .. k end
  end
  return 'none'
end)
local HALF = math.deg(math.atan(1.5 / (15 * math.cos(math.rad(22.5)))))
local function angdiff(a, b)
  local d = (a - b) % 360
  if d > 180 then d = 360 - d end
  return d
end
logic_define('wall_for_bearing', function(b)
  b = b % 360
  if angdiff(b, 347.5) <= HALF then return 'Gate_North' end
  if angdiff(b, 167.5) <= HALF then return 'Gate_South' end
  if b < 10 then b = b + 360 end
  return 'Wall' .. (math.floor((b - 10) / 45) + 1)
end)
";

        /// <summary>Tower centres hard-coded from the brief (scaled by the Keep's stud reading).</summary>
        private const string FortressHardcodedNearestLua = @"
logic_define('nearest_tower', function(name)
  local p = coreai_world_pos(name)
  local keep = coreai_world_pos('Keep')
  if p == nil or keep == nil then return 'none' end
  local studs = keep.x / 6
  local d = {}
  local best = nil
  for k = 1, 8 do
    local b = math.rad(10 + 45 * (k - 1))
    local tx = (6 + 15 * math.sin(b)) * studs
    local tz = -(-4 + 15 * math.cos(b)) * studs
    d[k] = math.sqrt((p.x - tx) ^ 2 + (p.z - tz) ^ 2)
    if best == nil or d[k] < best then best = d[k] end
  end
  for k = 1, 8 do
    if d[k] <= best * 1.01 then return 'Tower' .. k end
  end
  return 'none'
end)
";

        private const string DungeonHelpersLua = @"
local ROOM_LETTERS = { 'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I' }
local KEY_COLOURS = { 'Red', 'Blue', 'Gold' }
local function rooms()
  local list = {}
  for _, letter in ipairs(ROOM_LETTERS) do
    local name = 'Room_' .. letter
    local p = coreai_world_pos(name)
    if p ~= nil then list[#list + 1] = { name = name, x = p.x, z = p.z } end
  end
  return list
end
local function room_of(name)
  local p = coreai_world_pos(name)
  if p == nil then return 'none' end
  local list = rooms()
  local best = nil
  for _, r in ipairs(list) do
    r.d = math.sqrt((p.x - r.x) ^ 2 + (p.z - r.z) ^ 2)
    if best == nil or r.d < best then best = r.d end
  end
  for _, r in ipairs(list) do
    if r.d <= best * 1.01 then return r.name end
  end
  return 'none'
end
local function inventory(text)
  local set = {}
  for colour in string.gmatch(text or '', '[^,]+') do set[colour] = true end
  return set
end
local function door_info(name)
  local a, b, rest = string.match(name, '^Door_([A-I])_([A-I])(.*)$')
  if a == nil or a == b then return nil end
  local colour = nil
  if rest ~= '' then
    colour = string.match(rest, '^_(%a+)$')
    if colour ~= 'Red' and colour ~= 'Blue' and colour ~= 'Gold' then return nil end
  end
  return { a = 'Room_' .. a, b = 'Room_' .. b, colour = colour }
end
local function opens(colour, keys)
  return colour == nil or keys[colour] == true or keys.Gold == true
end
local function graph(keys)
  local present = {}
  local adj = {}
  for _, r in ipairs(rooms()) do
    present[r.name] = true
    adj[r.name] = {}
  end
  for _, n in ipairs(coreai_world_find('Door_')) do
    local d = door_info(n)
    if d ~= nil and present[d.a] and present[d.b] and opens(d.colour, keys) then
      table.insert(adj[d.a], d.b)
      table.insert(adj[d.b], d.a)
    end
  end
  return adj, present
end
local function distances(adj, from)
  local dist = { [from] = 0 }
  local queue = { from }
  local head = 1
  while head <= #queue do
    local u = queue[head]
    head = head + 1
    for _, v in ipairs(adj[u]) do
      if dist[v] == nil then
        dist[v] = dist[u] + 1
        queue[#queue + 1] = v
      end
    end
  end
  return dist
end
logic_define('room_of', room_of)
logic_define('can_open', function(door, inv)
  if not coreai_world_exists(door) then return false end
  local d = door_info(door)
  if d == nil then return false end
  return opens(d.colour, inventory(inv))
end)
logic_define('reachable', function(from, to, inv)
  local adj, present = graph(inventory(inv))
  if not present[from] or not present[to] then return false end
  return distances(adj, from)[to] ~= nil
end)
logic_define('next_step', function(from, to, inv)
  local adj, present = graph(inventory(inv))
  if not present[from] or not present[to] then return 'none' end
  if from == to then return from end
  local dist = distances(adj, to)
  if dist[from] == nil then return 'none' end
  local best = nil
  for _, n in ipairs(adj[from]) do
    if dist[n] == dist[from] - 1 and (best == nil or n < best) then best = n end
  end
  return best or 'none'
end)
";

        private const string DungeonMinDoorsLua = @"
logic_define('min_doors_to_exit', function(inv)
  local start = room_of('Player')
  local goal = room_of('Exit')
  if start == 'none' or goal == 'none' then return -1 end
  local keyRoom = {}
  for _, c in ipairs(KEY_COLOURS) do keyRoom[c] = room_of('Key_' .. c) end
  local function pick(room, held)
    local result = {}
    for _, c in ipairs(KEY_COLOURS) do result[c] = held[c] == true or keyRoom[c] == room end
    return result
  end
  local function tag(held)
    return (held.Red and 'R' or '-') .. (held.Blue and 'B' or '-') .. (held.Gold and 'G' or '-')
  end
  local graphs = {}
  local held0 = pick(start, inventory(inv))
  local seen = { [start .. tag(held0)] = true }
  local queue = { { room = start, held = held0, d = 0 } }
  local head = 1
  while head <= #queue do
    local s = queue[head]
    head = head + 1
    if s.room == goal then return s.d end
    local t = tag(s.held)
    if graphs[t] == nil then graphs[t] = graph(s.held) end
    for _, v in ipairs(graphs[t][s.room] or {}) do
      local h = pick(v, s.held)
      local k = v .. tag(h)
      if not seen[k] then
        seen[k] = true
        queue[#queue + 1] = { room = v, held = h, d = s.d + 1 }
      end
    end
  end
  return -1
end)
";

        /// <summary>The published answers memorised instead of searched.</summary>
        private const string DungeonHardcodedMinDoorsLua = @"
logic_define('min_doors_to_exit', function(inv)
  local known = { [''] = 11, Red = 9, Blue = 3, Gold = 3, ['Red,Blue'] = 3, ['Blue,Red'] = 3 }
  return known[inv] or -1
end)
";

        private const string GoldMasterRule = " or keys.Gold == true";


        [Test]
        public void Fortress_GoldenGeometry_MatchesHandComputedValues()
        {
            Dictionary<string, CoreAiSpawnBatchItem> items = ByName(FortressItems(new FortressOptions()));
            Assert.AreEqual(27, items.Count);
            AssertAt(items["Tower1"], 8.6047, 5.0, 10.7721);
            AssertAt(items["Tower5"], 3.3953, 5.0, -18.7721);
            AssertAt(items["Tower8"], -2.6036, 4.0, 8.2873);
            AssertAt(items["Wall1"], 13.4460, 2.0, 7.6879);
            Assert.AreEqual(8.4805, items["Wall1"].scaleX, 1e-3);
            Assert.AreEqual(32.5, items["Wall1"].ry, 1e-3);
            AssertAt(items["Gate_North"], 3.0005, 2.5, 9.5297);
            Assert.AreEqual(347.5, items["Gate_North"].ry, 1e-3);
            AssertAt(items["Wall4a"], 11.8016, 2.0, -16.9085);
            AssertAt(items["Wall8b"], 5.8026, 2.0, 10.1509);
            Assert.AreEqual(2.7403, items["Wall8b"].scaleX, 1e-3);
            AssertAt(items["Brazier1M"], 3.9513, 0.5, 2.0665);
            AssertAt(items["Brazier2M"], -0.3222, 0.5, -3.8273);
        }

        [Test]
        public void Fortress_GoldenSolution_ScoresFullBase()
        {
            WithScenario(GameBuildScenariosG1.All(), FortressId, (scenario, env) =>
            {
                BuildFortress(env, new FortressOptions());
                SeedLua(env, FortressLua);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertAllPassed(grading);
                Assert.AreEqual(0, grading.Penalties.Count);
                Assert.AreEqual(100.0, BaseScore(grading), 1e-9);
                Assert.Greater(grading.Bonus, 0.0);
            });
        }

        [Test]
        public void Fortress_MirrorAcrossTheNorthAxis_LosesOnlyTheMirroredBraziers()
        {
            WithScenario(GameBuildScenariosG1.All(), FortressId, (scenario, env) =>
            {
                BuildFortress(env, new FortressOptions { MirrorAcrossNorthAxis = true });
                SeedLua(env, FortressLua);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertOnlyFailures(grading, "mirrored_braziers");
            });
        }

        [Test]
        public void Fortress_MathConventionYaw_LosesEveryYawChecked()
        {
            WithScenario(GameBuildScenariosG1.All(), FortressId, (scenario, env) =>
            {
                BuildFortress(env, new FortressOptions { NegateYaw = true });
                SeedLua(env, FortressLua);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertOnlyFailures(grading, "walls_size_yaw", "gates", "gate_wall_segments");
            });
        }

        [Test]
        public void Fortress_HardcodedTowerPositions_FailOnlyTheLiveProbes()
        {
            WithScenario(GameBuildScenariosG1.All(), FortressId, (scenario, env) =>
            {
                BuildFortress(env, new FortressOptions());
                SeedLua(env, FortressLua + FortressHardcodedNearestLua);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertOnlyFailures(grading, "nearest_tower_live");
            });
        }

        [Test]
        public void Fortress_SlotRaisingOnlyOnTheBuiltFortress_KeepsTheReferenceCheckpoints()
        {
            WithScenario(GameBuildScenariosG1.All(), FortressId, (scenario, env) =>
            {
                const string anchor = "local p = coreai_world_pos(name)";
                StringAssert.Contains(anchor, FortressLua);
                BuildFortress(env, new FortressOptions());
                // WHY: only the own-world probes ask about Brazier1M, so only they hit the error.
                SeedLua(env, FortressLua.Replace(anchor,
                    "if name == 'Brazier1M' then error('raises on the built fortress only') end " + anchor));

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertOnlyFailures(grading, "own_world_consistency");
            });
        }

        [Test]
        public void Fortress_ExtraRootAndDuplicateTower_FailInventoryAndArePenalised()
        {
            WithScenario(GameBuildScenariosG1.All(), FortressId, (scenario, env) =>
            {
                BuildFortress(env, new FortressOptions { AddExtraAndDuplicate = true });
                SeedLua(env, FortressLua);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertOnlyFailures(grading, "exact_inventory");
                Assert.AreEqual(2, grading.Penalties.Count);
                Assert.Less(BaseScore(grading), 100.0 - 5.0);
            });
        }

        [Test]
        public void Fortress_GradingRestoresTheModelSceneAndRecord()
        {
            WithScenario(GameBuildScenariosG1.All(), FortressId, (scenario, env) =>
            {
                BuildFortress(env, new FortressOptions { MirrorAcrossNorthAxis = true });
                SeedLua(env, FortressLua);
                int commands = env.World.Commands.Count;
                List<object> names = env.World.FindObjectNames("");
                string brazier = JsonConvert.SerializeObject(env.World.ObjectPosition("Brazier1M"));
                string tower = JsonConvert.SerializeObject(env.World.ObjectPosition("Tower6"));

                ScenarioGrading first = scenario.Grade(env, SuccessfulRun());
                ScenarioGrading second = scenario.Grade(env, SuccessfulRun());

                Assert.AreEqual(commands, env.World.Commands.Count);
                CollectionAssert.AreEquivalent(names, env.World.FindObjectNames(""));
                Assert.AreEqual(brazier, JsonConvert.SerializeObject(env.World.ObjectPosition("Brazier1M")));
                Assert.AreEqual(tower, JsonConvert.SerializeObject(env.World.ObjectPosition("Tower6")));
                Assert.AreEqual(0, env.World.InvalidCommandCount);
                for (int i = 0; i < first.Checkpoints.Count; i++)
                {
                    Assert.AreEqual(first.Checkpoints[i].Passed, second.Checkpoints[i].Passed,
                        first.Checkpoints[i].Id);
                }
            });
        }

        [Test]
        public void Fortress_NothingBuilt_ScoresNearZero()
        {
            WithScenario(GameBuildScenariosG1.All(), FortressId, (scenario, env) =>
            {
                ScenarioGrading grading = scenario.Grade(env, new RunObservation());

                Assert.AreEqual(40, grading.HardCap);
                Assert.Less(BaseScore(grading), 10.0);
            });
        }


        [Test]
        public void Dungeon_GoldenSolution_ScoresFullBase()
        {
            WithScenario(GameIntegrationScenariosG7.All(), DungeonId, (scenario, env) =>
            {
                BuildDungeon(env, new DungeonOptions());
                SeedLua(env, DungeonHelpersLua + DungeonMinDoorsLua);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertAllPassed(grading);
                Assert.AreEqual(0, grading.Penalties.Count);
                Assert.AreEqual(100.0, BaseScore(grading), 1e-9);
            });
        }

        [Test]
        public void Dungeon_GoldenLua_GivesTheHandDerivedAnswers()
        {
            WithScenario(GameIntegrationScenariosG7.All(), DungeonId, (scenario, env) =>
            {
                BuildDungeon(env, new DungeonOptions());
                SeedLua(env, DungeonHelpersLua + DungeonMinDoorsLua);

                AssertNumber(env, 11, "");
                AssertNumber(env, 9, "Red");
                AssertNumber(env, 3, "Blue");
                AssertNumber(env, 3, "Gold");
                Assert.IsTrue(env.Lua.TryString("next_step", out string tie, "Room_E", "Room_C", "Red"));
                Assert.AreEqual("Room_B", tie);
                Assert.IsTrue(env.Lua.TryString("next_step", out string goldTie, "Room_G", "Room_C", "Gold"));
                Assert.AreEqual("Room_D", goldTie);
                Assert.IsTrue(env.Lua.TryString("room_of", out string doorRoom, "Door_A_B"));
                Assert.AreEqual("Room_A", doorRoom);
                Assert.IsTrue(env.Lua.TryBool("reachable", out bool locked, "Room_A", "Room_H", ""));
                Assert.IsFalse(locked);
            });
        }

        [Test]
        public void Dungeon_BlueKeyInTheFirstNewRoom_LosesOnlyKeyBlue()
        {
            WithScenario(GameIntegrationScenariosG7.All(), DungeonId, (scenario, env) =>
            {
                BuildDungeon(env, new DungeonOptions { BlueKeyRoom = 'C' });
                SeedLua(env, DungeonHelpersLua + DungeonMinDoorsLua);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertOnlyFailures(grading, "key_blue");
            });
        }

        [Test]
        public void Dungeon_MemorisedEscapeLengths_FailOnlyTheLiveProbes()
        {
            WithScenario(GameIntegrationScenariosG7.All(), DungeonId, (scenario, env) =>
            {
                BuildDungeon(env, new DungeonOptions());
                SeedLua(env, DungeonHelpersLua + DungeonHardcodedMinDoorsLua);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertOnlyFailures(grading, "live_keys_exit", "live_doors");
            });
        }

        [Test]
        public void Dungeon_SlotRaisingOnlyOnTheBuiltDungeon_KeepsTheReferenceCheckpoints()
        {
            WithScenario(GameIntegrationScenariosG7.All(), DungeonId, (scenario, env) =>
            {
                const string anchor = "local goal = room_of('Exit')";
                string lua = DungeonHelpersLua + DungeonMinDoorsLua;
                StringAssert.Contains(anchor, lua);
                BuildDungeon(env, new DungeonOptions { BlueKeyRoom = 'C' });
                // WHY: Key_Blue sits in Room_C only in the model's own dungeon, never in the reference copy.
                SeedLua(env, lua.Replace(anchor, anchor +
                    " if room_of('Key_Blue') == 'Room_C' then error('raises on the built dungeon only') end"));

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertOnlyFailures(grading, "key_blue", "own_world_consistency");
            });
        }

        [Test]
        public void Dungeon_GoldWithoutMasterRule_FailsTheLockLogic()
        {
            WithScenario(GameIntegrationScenariosG7.All(), DungeonId, (scenario, env) =>
            {
                string lua = DungeonHelpersLua + DungeonMinDoorsLua;
                StringAssert.Contains(GoldMasterRule, lua);
                BuildDungeon(env, new DungeonOptions());
                SeedLua(env, lua.Replace(GoldMasterRule, ""));

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                // WHY: live_keys_exit still passes because its probes only hold Gold where Gold's own doors decide.
                AssertOnlyFailures(grading, "can_open", "reachable", "next_step", "min_doors", "live_doors",
                    "own_world_consistency");
            });
        }

        [Test]
        public void Dungeon_ExtraDoorAndDuplicateKey_FailInventoryAndArePenalised()
        {
            WithScenario(GameIntegrationScenariosG7.All(), DungeonId, (scenario, env) =>
            {
                BuildDungeon(env, new DungeonOptions { AddExtraAndDuplicate = true });
                SeedLua(env, DungeonHelpersLua + DungeonMinDoorsLua);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertOnlyFailures(grading, "doors_set", "exact_inventory");
                Assert.AreEqual(2, grading.Penalties.Count);
            });
        }

        [Test]
        public void Dungeon_GradingRestoresTheModelSceneAndRecord()
        {
            WithScenario(GameIntegrationScenariosG7.All(), DungeonId, (scenario, env) =>
            {
                BuildDungeon(env, new DungeonOptions { BlueKeyRoom = 'C' });
                SeedLua(env, DungeonHelpersLua + DungeonMinDoorsLua);
                int commands = env.World.Commands.Count;
                List<object> names = env.World.FindObjectNames("");
                string key = JsonConvert.SerializeObject(env.World.ObjectPosition("Key_Blue"));

                scenario.Grade(env, SuccessfulRun());

                Assert.AreEqual(commands, env.World.Commands.Count);
                CollectionAssert.AreEquivalent(names, env.World.FindObjectNames(""));
                Assert.AreEqual(key, JsonConvert.SerializeObject(env.World.ObjectPosition("Key_Blue")));
                Assert.AreEqual(0, env.World.InvalidCommandCount);
            });
        }


        [Test]
        public void SpawnArena_TwoEnemiesOnTheSameSpot_FailSpatialSpread()
        {
            WithScenario(GameBuildScenariosG1.All(), "g1_spawn_arena", (scenario, env) =>
            {
                Spawn(env, "Player", "capsule", 0f, 1f, 0f);
                Spawn(env, "Enemy1", "cube", 3f, 1f, 0f);
                Spawn(env, "Enemy2", "cube", 3.4f, 1f, 0f);
                Spawn(env, "Enemy3", "cube", -3f, 1f, 0f);
                Spawn(env, "Enemy4", "cube", 0f, 1f, 3f);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertCheckpoint(grading, "four_enemies", true);
                AssertCheckpoint(grading, "exact_five", true);
                AssertCheckpoint(grading, "spatial_spread", false);
            });
        }

        [Test]
        public void SpawnArena_SpreadOutObjects_PassSpatialSpread()
        {
            WithScenario(GameBuildScenariosG1.All(), "g1_spawn_arena", (scenario, env) =>
            {
                Spawn(env, "Player", "capsule", 0f, 1f, 0f);
                Spawn(env, "Enemy1", "cube", 3f, 1f, 0f);
                Spawn(env, "Enemy2", "cube", 4f, 1f, 0f);
                Spawn(env, "Enemy3", "cube", -3f, 1f, 0f);
                Spawn(env, "Enemy4", "cube", 0f, 1f, 3f);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertAllPassed(grading);
            });
        }

        [Test]
        public void CoinCollector_CoinDestroyedAfterSpawning_DoesNotCount()
        {
            WithScenario(GameBuildScenariosG1.All(), "g1_coin_collector", (scenario, env) =>
            {
                Spawn(env, "Player", "capsule", 0f, 1f, 0f);
                Spawn(env, "Coin1", "sphere", 2f, 1f, 0f);
                Spawn(env, "Coin2", "sphere", 4f, 1f, 0f);
                Spawn(env, "Coin3", "sphere", 6f, 1f, 0f);
                Spawn(env, "Goal", "cube", 8f, 1f, 0f);
                Execute(env, CoreAiWorldCommandEnvelope.Destroy("Coin2"));
                SeedLua(env, "logic_define('score_formula', function(n) return n end) " +
                             "logic_define('win_condition', function(s) return s >= 3 end)");

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertCheckpoint(grading, "three_coins", false);
                AssertCheckpoint(grading, "score_formula", true);
                AssertCheckpoint(grading, "win_at_true", true);
            });
        }

        [Test]
        public void CoinCollector_LookupTableOfSmallCounts_FailsScoreFormula()
        {
            WithScenario(GameBuildScenariosG1.All(), "g1_coin_collector", (scenario, env) =>
            {
                SeedLua(env, "local t = {[0]=0,1,2,3,4,5,6,7,8,9} " +
                             "logic_define('score_formula', function(n) return t[n] or 0 end)");

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertCheckpoint(grading, "score_formula", false);
            });
        }

        [Test]
        public void ConstraintBudget_WrongPrefabs_FailPrefabKeys()
        {
            WithScenario(GameBuildScenariosG1.All(), "g1_constraint_budget", (scenario, env) =>
            {
                Spawn(env, "Tree", "cube", 0f, 1f, 0f);
                Spawn(env, "Rock", "sphere", 2f, 0.5f, 0f);
                Spawn(env, "Bush", "sphere", 4f, 0.5f, 0f);

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertCheckpoint(grading, "three_named", true);
                AssertCheckpoint(grading, "spatial_spread", true);
                AssertCheckpoint(grading, "prefab_keys", false);
            });
        }

        [Test]
        public void KeyPuzzle_DistanceOnTheXAxisOnly_FailsUnitAndIntegration()
        {
            WithScenario(GameIntegrationScenariosG7.All(), "g7_key_puzzle", (scenario, env) =>
            {
                BuildKeyPuzzle(env, 1f);
                SeedLua(env, "logic_define('key_found', function(px, pz, kx, kz) " +
                             "return math.abs(px - kx) <= 2.0 end)");

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertCheckpoint(grading, "unit_correctness", false);
                AssertCheckpoint(grading, "integration_consistency", false);
            });
        }

        [Test]
        public void KeyPuzzle_CorrectSolution_PassesEveryCheck()
        {
            WithScenario(GameIntegrationScenariosG7.All(), "g7_key_puzzle", (scenario, env) =>
            {
                BuildKeyPuzzle(env, 1f);
                SeedLua(env, "logic_define('key_found', function(px, pz, kx, kz) " +
                             "return math.sqrt((px - kx)^2 + (pz - kz)^2) <= 2.0 end)");

                ScenarioGrading grading = scenario.Grade(env, SuccessfulRun());

                AssertAllPassed(grading);
            });
        }

        [Test]
        public void KeyPuzzle_WrongHeightOrKeyMovedOutOfItsZone_FailZoneCompliance()
        {
            WithScenario(GameIntegrationScenariosG7.All(), "g7_key_puzzle", (scenario, env) =>
            {
                BuildKeyPuzzle(env, 0f);
                ScenarioGrading wrongHeight = scenario.Grade(env, SuccessfulRun());
                AssertCheckpoint(wrongHeight, "zone_compliance", false);
            });

            WithScenario(GameIntegrationScenariosG7.All(), "g7_key_puzzle", (scenario, env) =>
            {
                BuildKeyPuzzle(env, 1f);
                Execute(env, CoreAiWorldCommandEnvelope.Change("Key", new Vector3(0f, 1f, 6f), true,
                    Vector3.zero, false, 0f, Vector3.zero, false, null));
                ScenarioGrading moved = scenario.Grade(env, SuccessfulRun());
                AssertCheckpoint(moved, "zone_compliance", false);
            });
        }

        [Test]
        public void ExtremeScenarios_AreFlaggedAndAppendedToTheirGroups()
        {
            GameBenchmarkScenario[] g1 = GameBuildScenariosG1.All();
            GameBenchmarkScenario[] g7 = GameIntegrationScenariosG7.All();
            foreach (GameBenchmarkScenario extreme in new[] { g1[g1.Length - 1], g7[g7.Length - 1] })
            {
                Assert.IsTrue(extreme.IsExtreme, extreme.Id);
                Assert.AreEqual(5, extreme.Difficulty, extreme.Id);
                Assert.AreEqual(480f, extreme.TimeoutSeconds, extreme.Id);
                Assert.IsFalse(extreme.CaptureScene, extreme.Id);
                StringAssert.StartsWith("Extreme:", extreme.Name, extreme.Id);
                Assert.IsNotEmpty(extreme.WhatItChecks, extreme.Id);
            }

            Assert.AreEqual(FortressId, g1[g1.Length - 1].Id);
            Assert.AreEqual(DungeonId, g7[g7.Length - 1].Id);
            for (int i = 0; i < g1.Length - 1; i++)
            {
                Assert.IsFalse(g1[i].IsExtreme, g1[i].Id);
            }

            for (int i = 0; i < g7.Length - 1; i++)
            {
                Assert.IsFalse(g7[i].IsExtreme, g7[i].Id);
            }
        }


        private sealed class FortressOptions
        {
            public bool MirrorAcrossNorthAxis;
            public bool NegateYaw;
            public bool AddExtraAndDuplicate;
        }

        private sealed class DungeonOptions
        {
            public char BlueKeyRoom = 'I';
            public bool AddExtraAndDuplicate;
        }

        private static void BuildFortress(BenchmarkEnvironment env, FortressOptions options)
        {
            List<CoreAiSpawnBatchItem> items = FortressItems(options);
            SpawnBatch(env, items);
            if (options.AddExtraAndDuplicate)
            {
                Spawn(env, "FortressRoot", "empty", 6f, 0f, -4f);
                CoreAiSpawnBatchItem tower3 = ByName(items)["Tower3"];
                SpawnBatch(env, new List<CoreAiSpawnBatchItem> { tower3 });
            }
        }

        /// <summary>
        /// The fortress worked out by hand: towers by successive 45-degree turns of Tower1's offset, wall
        /// pieces by walking along each chord from its towers, yaw from the chord direction, mirrors by
        /// reflecting each brazier's bearing about bearing 10.
        /// </summary>
        private static List<CoreAiSpawnBatchItem> FortressItems(FortressOptions options)
        {
            const double cx = 6.0;
            const double cz = -4.0;
            List<CoreAiSpawnBatchItem> items = new()
            {
                Item("Keep", "cube", cx, 3.0, cz, 6.0, 6.0, 6.0, 0.0)
            };

            double[] tx = new double[10];
            double[] tz = new double[10];
            double ox = 15.0 * Math.Sin(Radians(10.0));
            double oz = 15.0 * Math.Cos(Radians(10.0));
            double turn = Radians(45.0);
            for (int k = 1; k <= 8; k++)
            {
                tx[k] = cx + ox;
                tz[k] = cz + oz;
                double height = k % 2 == 1 ? 5.0 : 4.0;
                items.Add(Item($"Tower{k}", "cylinder", tx[k], height, tz[k], 3.0, height, 3.0, 0.0));
                double nx = ox * Math.Cos(turn) + oz * Math.Sin(turn);
                double nz = -ox * Math.Sin(turn) + oz * Math.Cos(turn);
                ox = nx;
                oz = nz;
            }

            tx[9] = tx[1];
            tz[9] = tz[1];
            double yawSign = options.NegateYaw ? -1.0 : 1.0;
            for (int k = 1; k <= 8; k++)
            {
                double dx = tx[k + 1] - tx[k];
                double dz = tz[k + 1] - tz[k];
                double chord = Math.Sqrt(dx * dx + dz * dz);
                double yaw = yawSign * Normalize(Degrees(Math.Atan2(dx, dz)) - 90.0);
                double mx = (tx[k] + tx[k + 1]) / 2.0;
                double mz = (tz[k] + tz[k + 1]) / 2.0;
                if (k != 4 && k != 8)
                {
                    items.Add(Item($"Wall{k}", "cube", mx, 2.0, mz, chord - 3.0, 4.0, 1.5, yaw));
                    continue;
                }

                double piece = (chord - 6.0) / 2.0;
                double along = (1.5 + piece / 2.0) / chord;
                items.Add(Item(k == 8 ? "Gate_North" : "Gate_South", "cube", mx, 2.5, mz, 3.0, 5.0, 2.5, yaw));
                items.Add(Item($"Wall{k}a", "cube", tx[k] + dx * along, 2.0, tz[k] + dz * along, piece, 4.0, 1.5,
                    yaw));
                items.Add(Item($"Wall{k}b", "cube", tx[k + 1] - dx * along, 2.0, tz[k + 1] - dz * along, piece, 4.0,
                    1.5, yaw));
            }

            double[,] offsets = { { 4.0, 5.0 }, { 6.0, -2.0 }, { -1.0, -7.0 } };
            for (int i = 0; i < 3; i++)
            {
                double vx = offsets[i, 0];
                double vz = offsets[i, 1];
                items.Add(Item($"Brazier{i + 1}", "sphere", cx + vx, 0.5, cz + vz, 1.0, 1.0, 1.0, 0.0));
                double mirroredX;
                double mirroredZ;
                if (options.MirrorAcrossNorthAxis)
                {
                    mirroredX = -vx;
                    mirroredZ = vz;
                }
                else
                {
                    double radius = Math.Sqrt(vx * vx + vz * vz);
                    double bearing = Radians(2.0 * 10.0 - Degrees(Math.Atan2(vx, vz)));
                    mirroredX = radius * Math.Sin(bearing);
                    mirroredZ = radius * Math.Cos(bearing);
                }

                items.Add(Item($"Brazier{i + 1}M", "sphere", cx + mirroredX, 0.5, cz + mirroredZ, 1.0, 1.0, 1.0, 0.0));
            }

            return items;
        }

        private static void BuildDungeon(BenchmarkEnvironment env, DungeonOptions options)
        {
            const string letters = "ABCDEFGHI";
            List<CoreAiSpawnBatchItem> items = new();
            for (int i = 0; i < letters.Length; i++)
            {
                (double x, double z) = RoomCentre(letters[i]);
                items.Add(Item("Room_" + letters[i], "cube", x, -0.25, z, 8.0, 0.5, 8.0, 0.0));
            }

            string[] doors =
            {
                "Door_A_B", "Door_B_C_Red", "Door_A_D_Blue", "Door_B_E", "Door_D_E", "Door_E_F_Red", "Door_C_F",
                "Door_D_G_Blue", "Door_F_I", "Door_G_H_Gold", "Door_H_I_Gold"
            };
            foreach (string door in doors)
            {
                (double ax, double az) = RoomCentre(door[5]);
                (double bx, double bz) = RoomCentre(door[7]);
                bool eastWest = Math.Abs(az - bz) < 1e-9;
                items.Add(Item(door, "cube", (ax + bx) / 2.0, 1.5, (az + bz) / 2.0, eastWest ? 2.0 : 3.0, 3.0,
                    eastWest ? 3.0 : 2.0, 0.0));
            }

            items.Add(KeyItem("Key_Red", 'D'));
            items.Add(KeyItem("Key_Blue", options.BlueKeyRoom));
            items.Add(KeyItem("Key_Gold", 'G'));
            items.Add(Item("Player", "capsule", -10.0, 1.0, 5.0, 1.0, 1.0, 1.0, 0.0));
            items.Add(Item("Exit", "cylinder", 0.0, 0.5, 25.0, 2.0, 0.5, 2.0, 0.0));
            SpawnBatch(env, items);

            if (options.AddExtraAndDuplicate)
            {
                items = new List<CoreAiSpawnBatchItem>
                {
                    Item("Door_E_H_Red", "cube", 0.0, 1.5, 20.0, 3.0, 3.0, 2.0, 0.0),
                    KeyItem("Key_Red", 'D')
                };
                SpawnBatch(env, items);
            }
        }

        private static (double x, double z) RoomCentre(char letter)
        {
            int index = letter - 'A';
            return (10.0 * (index % 3) - 10.0, 10.0 * (index / 3) + 5.0);
        }

        private static CoreAiSpawnBatchItem KeyItem(string name, char room)
        {
            (double x, double z) = RoomCentre(room);
            return new CoreAiSpawnBatchItem
            {
                prefabKey = "sphere", name = name, x = (float)(x + 2.0), y = 0.5f, z = (float)(z + 2.0), scale = 0.5f
            };
        }

        private static void BuildKeyPuzzle(BenchmarkEnvironment env, float height)
        {
            Spawn(env, "Player", "capsule", 0f, height, -6f);
            Spawn(env, "Gate", "cube", 0f, height, 6f);
            Spawn(env, "Key", "sphere", 1f, height, 0.5f);
        }


        private static CoreAiSpawnBatchItem Item(string name, string prefab, double x, double y, double z,
            double sx, double sy, double sz, double yaw)
        {
            return new CoreAiSpawnBatchItem
            {
                prefabKey = prefab,
                name = name,
                x = (float)x,
                y = (float)y,
                z = (float)z,
                ry = (float)yaw,
                scaleX = (float)sx,
                scaleY = (float)sy,
                scaleZ = (float)sz
            };
        }

        private static Dictionary<string, CoreAiSpawnBatchItem> ByName(List<CoreAiSpawnBatchItem> items)
        {
            Dictionary<string, CoreAiSpawnBatchItem> map = new();
            foreach (CoreAiSpawnBatchItem item in items)
            {
                map[item.name] = item;
            }

            return map;
        }

        private static void SpawnBatch(BenchmarkEnvironment env, List<CoreAiSpawnBatchItem> items)
        {
            Execute(env, CoreAiWorldCommandEnvelope.SpawnBatch("", "", Vector3.zero, Vector3.zero, 0f, Vector3.zero,
                "", items.ToArray()));
        }

        private static void Spawn(BenchmarkEnvironment env, string name, string prefab, float x, float y, float z)
        {
            Execute(env, CoreAiWorldCommandEnvelope.Spawn(prefab, name, new Vector3(x, y, z)));
        }

        private static void Execute(BenchmarkEnvironment env, CoreAiWorldCommandEnvelope envelope)
        {
            Assert.IsTrue(env.World.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = JsonConvert.SerializeObject(envelope)
            }));
        }

        private static void SeedLua(BenchmarkEnvironment env, string lua)
        {
            env.Lua.Seed(lua);
            Assert.AreEqual(0, env.Lua.FailedExecutions, env.Lua.LastError);
        }

        private static void AssertNumber(BenchmarkEnvironment env, double expected, string inventory)
        {
            Assert.IsTrue(env.Lua.TryNumber("min_doors_to_exit", out double value, inventory), env.Lua.LastError);
            Assert.AreEqual(expected, value, 1e-9, $"min_doors_to_exit('{inventory}')");
        }

        private static void AssertAt(CoreAiSpawnBatchItem item, double x, double y, double z)
        {
            Assert.AreEqual(x, item.x, 1e-3, item.name + ".x");
            Assert.AreEqual(y, item.y, 1e-3, item.name + ".y");
            Assert.AreEqual(z, item.z, 1e-3, item.name + ".z");
        }

        private static RunObservation SuccessfulRun()
        {
            return new RunObservation { Turns = 2, ToolCalls = 3 };
        }

        private static double BaseScore(ScenarioGrading grading)
        {
            return GoalScore.Compute(grading.Checkpoints, grading.Penalties, grading.Bonus, grading.HardCap).Base;
        }

        private static void AssertCheckpoint(ScenarioGrading grading, string id, bool expected)
        {
            BenchmarkCheckpoint checkpoint = grading.Checkpoints.Find(c => c.Id == id);
            Assert.IsNotNull(checkpoint, $"Missing checkpoint '{id}'.");
            Assert.AreEqual(expected, checkpoint.Passed, $"{id}: {checkpoint.Detail}");
        }

        private static void AssertAllPassed(ScenarioGrading grading)
        {
            foreach (BenchmarkCheckpoint checkpoint in grading.Checkpoints)
            {
                Assert.IsTrue(checkpoint.Passed, $"{checkpoint.Id}: {checkpoint.Detail}");
            }
        }

        /// <summary>Exactly the named checkpoints fail; every other one passes.</summary>
        private static void AssertOnlyFailures(ScenarioGrading grading, params string[] failing)
        {
            HashSet<string> expected = new(failing);
            foreach (string id in failing)
            {
                Assert.IsNotNull(grading.Checkpoints.Find(c => c.Id == id), $"Missing checkpoint '{id}'.");
            }

            foreach (BenchmarkCheckpoint checkpoint in grading.Checkpoints)
            {
                Assert.AreEqual(!expected.Contains(checkpoint.Id), checkpoint.Passed,
                    $"{checkpoint.Id}: {checkpoint.Detail}");
            }
        }

        private static void WithScenario(GameBenchmarkScenario[] group, string scenarioId,
            Action<GameBenchmarkScenario, BenchmarkEnvironment> body)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                GameBenchmarkScenario scenario = Array.Find(group, candidate => candidate.Id == scenarioId);
                Assert.IsNotNull(scenario, scenarioId);
                BenchmarkEnvironment env = new(settings);
                scenario.Prepare(env);
                body(scenario, env);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        private static double Radians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static double Degrees(double radians)
        {
            return radians * 180.0 / Math.PI;
        }

        private static double Normalize(double degrees)
        {
            double d = degrees % 360.0;
            return d < 0.0 ? d + 360.0 : d;
        }
    }
}
#endif
#endif
