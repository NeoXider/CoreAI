#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using CoreAI.Benchmarking;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using static CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkHarness;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// Grader tests for the extreme tier of G2/G3/G4 and for the hardened regular G2/G3/G4 scenarios. A
    /// golden Lua solution, written independently of the C# references the graders compare against, must
    /// score a full base; near-miss solutions (one rule wrong, examples hardcoded) must lose exactly the
    /// checkpoints of what they get wrong.
    /// </summary>
    public sealed class ExtremeLogicScenariosPlayModeTests
    {
        // ---------------------------------------------------------------------------------------------
        //  G2 extreme: damage pipeline
        // ---------------------------------------------------------------------------------------------

        private const string DamagePipelineGolden = @"
local MATRIX = {
  physical = { fire = 100, water = 100, nature = 100, neutral = 100, ghost = 0 },
  fire = { fire = 50, water = 50, nature = 200, neutral = 100, ghost = 100 },
  water = { fire = 200, water = 50, nature = 50, neutral = 100, ghost = 100 },
  nature = { fire = 50, water = 200, nature = 50, neutral = 100, ghost = 100 },
}
local floor = math.floor
local function clamp(x, lo, hi)
  if x < lo then return lo end
  if x > hi then return hi end
  return x
end
local function resolve(attacker, defender, skill)
  local critChance = attacker.crit_chance or 0
  local critMult = attacker.crit_mult or 150
  local penPct = attacker.pen_pct or 0
  local penFlat = attacker.pen_flat or 0
  local resist = defender.resist or {}
  local guarding = defender.guarding == true
  local element = skill.element or 'physical'
  local hits = skill.hits or 1
  local trueDamage = skill.true_damage == true
  local seed = skill.seed or 0
  local m = MATRIX[element][defender.element]
  local r = clamp(resist[element] or 0, -50, 80)
  local diff = clamp(attacker.level - defender.level, -10, 10)
  local hp = defender.hp
  local shield = defender.shield or 0
  local out = { absorbed = 0, overkill = 0, crits = 0, hits = 0, killed = false }
  for k = 1, hits do
    if hp <= 0 then break end
    out.hits = out.hits + 1
    local d = floor(attacker.atk * math.max(10, skill.power - 20 * (k - 1)) / 100)
    d = floor(d * (100 + 3 * diff) / 100)
    local crit = (seed + 17 * k) % 100 < critChance
    if crit then
      out.crits = out.crits + 1
      d = floor(d * critMult / 100)
    end
    local immune = false
    if not trueDamage then
      local pen = penPct
      if crit then pen = pen * 2 end
      if pen > 100 then pen = 100 end
      local ea = floor(defender.armor * (100 - pen) / 100) - penFlat
      if ea < 0 then ea = 0 end
      d = floor(d * 100 / (100 + ea))
      d = floor(d * m / 100)
      d = floor(d * (100 - r) / 100)
      immune = m == 0
    end
    if guarding then d = floor(d / 2) end
    if d < 1 and not immune then d = 1 end
    local a = math.min(shield, d)
    shield = shield - a
    out.absorbed = out.absorbed + a
    local loss = d - a
    if a > 0 and shield == 0 then loss = floor(loss * 125 / 100) end
    if loss >= hp then
      out.overkill = loss - hp
      hp = 0
      out.killed = true
    else
      hp = hp - loss
    end
  end
  out.damage = defender.hp - hp
  out.hp = hp
  out.shield = shield
  return out
end
logic_define('resolve_attack', resolve)
";

        [Test]
        public void DamagePipeline_GoldenSolution_ScoresFullBase()
        {
            ScenarioGrading grading = GradeWith(GameMechanicScenariosG2.All(), "g2_extreme_damage_pipeline",
                DamagePipelineGolden);

            AssertFullBase(grading);
            Assert.Greater(grading.Bonus, 0);
        }

        [Test]
        public void DamagePipeline_PromptExamples_MatchTheReference()
        {
            ScenarioGrading grading = GradeWith(GameMechanicScenariosG2.All(), "g2_extreme_damage_pipeline",
                DamagePipelineGolden + @"
local e1 = resolve({ atk = 40, level = 12, crit_chance = 30, crit_mult = 150, pen_pct = 25, pen_flat = 5 },
  { hp = 60, shield = 10, armor = 40, level = 10, element = 'nature', resist = { fire = 20 }, guarding = false },
  { power = 120, element = 'fire', hits = 2, true_damage = false, seed = 3 })
local e2 = resolve({ atk = 90, level = 3, crit_chance = 50, crit_mult = 200, pen_pct = 40, pen_flat = 10 },
  { hp = 300, shield = 25, armor = 60, level = 20, element = 'fire', resist = { water = -30 }, guarding = true },
  { power = 100, element = 'water', hits = 3, true_damage = false, seed = 40 })
assert(e1.damage == 60 and e1.absorbed == 10 and e1.hp == 0 and e1.shield == 0 and e1.overkill == 57
  and e1.crits == 1 and e1.hits == 1 and e1.killed == true, 'example 1')
assert(e2.damage == 137 and e2.absorbed == 25 and e2.hp == 163 and e2.shield == 0 and e2.overkill == 0
  and e2.crits == 0 and e2.hits == 3 and e2.killed == false, 'example 2')
");

            AssertFullBase(grading);
        }

        [Test]
        public void DamagePipeline_FlatPenetrationBeforePercentage_LosesArmorFamilies()
        {
            ScenarioGrading grading = GradeWith(GameMechanicScenariosG2.All(), "g2_extreme_damage_pipeline",
                Mutate(DamagePipelineGolden,
                    "local ea = floor(defender.armor * (100 - pen) / 100) - penFlat",
                    "local ea = floor((defender.armor - penFlat) * (100 - pen) / 100)"));

            AssertCheckpoint(grading, "armor_penetration", false);
            AssertCheckpoint(grading, "full_pipeline", false);
            AssertCheckpoint(grading, "base_formula", true);
            AssertCheckpoint(grading, "level_scaling", true);
            AssertCheckpoint(grading, "elements_resist", true);
            AssertCheckpoint(grading, "multi_hit_shield", true);
            AssertCheckpoint(grading, "optional_fields", true);
            AssertPartialBase(grading);
        }

        [Test]
        public void DamagePipeline_NoShatter_LosesShieldFamilies()
        {
            ScenarioGrading grading = GradeWith(GameMechanicScenariosG2.All(), "g2_extreme_damage_pipeline",
                Mutate(DamagePipelineGolden,
                    "if a > 0 and shield == 0 then loss = floor(loss * 125 / 100) end", ""));

            AssertCheckpoint(grading, "multi_hit_shield", false);
            AssertCheckpoint(grading, "full_pipeline", false);
            AssertCheckpoint(grading, "base_formula", true);
            AssertCheckpoint(grading, "armor_penetration", true);
            AssertCheckpoint(grading, "critical_hits", true);
            AssertCheckpoint(grading, "edge_cases", true);
            AssertPartialBase(grading);
        }

        [Test]
        public void DamagePipeline_MinimumAppliedToImmuneHits_LosesEdgeCasesOnly()
        {
            ScenarioGrading grading = GradeWith(GameMechanicScenariosG2.All(), "g2_extreme_damage_pipeline",
                Mutate(DamagePipelineGolden, "if d < 1 and not immune then d = 1 end", "if d < 1 then d = 1 end"));

            AssertCheckpoint(grading, "edge_cases", false);
            AssertCheckpoint(grading, "full_pipeline", false);
            AssertCheckpoint(grading, "elements_resist", true);
            AssertCheckpoint(grading, "multi_hit_shield", true);
            AssertCheckpoint(grading, "optional_fields", true);
        }

        [Test]
        public void DamagePipeline_NilResistNotDefaulted_LosesOnlyTheLastFamily()
        {
            ScenarioGrading grading = GradeWith(GameMechanicScenariosG2.All(), "g2_extreme_damage_pipeline",
                Mutate(DamagePipelineGolden, "local resist = defender.resist or {}", "local resist = defender.resist"));

            AssertCheckpoint(grading, "optional_fields", false);
            foreach (string id in new[]
                     {
                         "result_table", "base_formula", "level_scaling", "armor_penetration", "elements_resist",
                         "critical_hits", "multi_hit_shield", "full_pipeline", "edge_cases"
                     })
            {
                AssertCheckpoint(grading, id, true);
            }
        }

        [Test]
        public void DamagePipeline_HardcodedExamples_FailsEveryHiddenFamily()
        {
            ScenarioGrading grading = GradeWith(GameMechanicScenariosG2.All(), "g2_extreme_damage_pipeline", @"
logic_define('resolve_attack', function(attacker, defender, skill)
  if attacker.atk == 40 then
    return { damage = 60, absorbed = 10, hp = 0, shield = 0, overkill = 57, crits = 1, hits = 1, killed = true }
  end
  return { damage = 137, absorbed = 25, hp = 163, shield = 0, overkill = 0, crits = 0, hits = 3, killed = false }
end)");

            foreach (string id in new[]
                     {
                         "result_table", "base_formula", "level_scaling", "armor_penetration", "elements_resist",
                         "critical_hits", "multi_hit_shield", "full_pipeline", "edge_cases", "optional_fields"
                     })
            {
                AssertCheckpoint(grading, id, false);
            }

            Assert.Less(Base(grading), 20);
        }

        // ---------------------------------------------------------------------------------------------
        //  G3 extreme: dungeon route planner
        // ---------------------------------------------------------------------------------------------

        private const string RouteGolden = @"
local function solve(grid, teleporters)
  local rows, cols = #grid, #grid[1]
  local cell, sr, sc = {}, 1, 1
  for r = 1, rows do
    cell[r] = {}
    for c = 1, cols do
      local ch = grid[r]:sub(c, c)
      cell[r][c] = ch
      if ch == 'S' then sr, sc = r, c end
    end
  end
  local tele = {}
  for _, t in ipairs(teleporters) do
    local key = t.fr * 100 + t.fc
    tele[key] = tele[key] or {}
    tele[key][#tele[key] + 1] = t
  end
  local KEY = { a = 1, b = 2 }
  local DOOR = { A = 1, B = 2 }
  local ARROW = { ['^'] = 'U', v = 'D', ['<'] = 'L', ['>'] = 'R' }
  local function holds(mask, bit) return math.floor(mask / bit) % 2 == 1 end
  local cost, acts, done, open = {}, {}, {}, {}
  local function relax(fromId, r, c, mask, step, move)
    local ch = cell[r][c]
    if ch == '#' then return end
    if DOOR[ch] and not holds(mask, DOOR[ch]) then return end
    if move and ARROW[ch] and ARROW[ch] ~= move then return end
    if KEY[ch] and not holds(mask, KEY[ch]) then mask = mask + KEY[ch] end
    local id = ((r - 1) * cols + c - 1) * 4 + mask
    if done[id] then return end
    local nc, na = cost[fromId] + step, acts[fromId] + 1
    if cost[id] == nil then
      open[#open + 1] = { r, c, mask, id }
    elseif nc > cost[id] or (nc == cost[id] and na >= acts[id]) then
      return
    end
    cost[id], acts[id] = nc, na
  end
  local startId = ((sr - 1) * cols + sc - 1) * 4
  cost[startId], acts[startId] = 0, 0
  open[1] = { sr, sc, 0, startId }
  while #open > 0 do
    local bi = 1
    for i = 2, #open do
      local a, b = open[i][4], open[bi][4]
      if cost[a] < cost[b] or (cost[a] == cost[b] and acts[a] < acts[b]) then bi = i end
    end
    local cur = table.remove(open, bi)
    local r, c, mask, id = cur[1], cur[2], cur[3], cur[4]
    done[id] = true
    if cell[r][c] == 'G' then return { cost = cost[id], actions = acts[id] } end
    if r > 1 then relax(id, r - 1, c, mask, cell[r - 1][c] == '~' and 3 or 1, 'U') end
    if r < rows then relax(id, r + 1, c, mask, cell[r + 1][c] == '~' and 3 or 1, 'D') end
    if c > 1 then relax(id, r, c - 1, mask, cell[r][c - 1] == '~' and 3 or 1, 'L') end
    if c < cols then relax(id, r, c + 1, mask, cell[r][c + 1] == '~' and 3 or 1, 'R') end
    for _, t in ipairs(tele[r * 100 + c] or {}) do relax(id, t.tr, t.tc, mask, t.cost) end
  end
  return { cost = -1, actions = -1 }
end
logic_define('route', solve)
";

        // WHY: the plainest array Dijkstra a model may write — every state scanned on every pop. It must
        // still fit the per-call instruction budget the goal states, or the task would punish a correct
        // algorithm for being simple.
        private const string RouteNaiveFullScan = @"
logic_define('route', function(grid, teleporters)
  local rows, cols = #grid, #grid[1]
  local n = rows * cols * 4
  local cost, acts, done = {}, {}, {}
  for i = 0, n - 1 do cost[i] = math.huge; acts[i] = math.huge; done[i] = false end
  local function at(r, c) return string.sub(grid[r], c, c) end
  local function id(r, c, m) return ((r - 1) * cols + (c - 1)) * 4 + m end
  local function has(m, bit) return math.floor(m / bit) % 2 == 1 end
  for r = 1, rows do for c = 1, cols do
    if at(r, c) == 'S' then cost[id(r, c, 0)] = 0; acts[id(r, c, 0)] = 0 end
  end end
  local arrow = { ['^'] = 1, v = 2, ['<'] = 3, ['>'] = 4 }
  local function relax(i, r, c, m, step, dir)
    local x = at(r, c)
    if x == '#' or (x == 'A' and not has(m, 1)) or (x == 'B' and not has(m, 2)) then return end
    if dir and arrow[x] and arrow[x] ~= dir then return end
    if x == 'a' and not has(m, 1) then m = m + 1 end
    if x == 'b' and not has(m, 2) then m = m + 2 end
    local j = id(r, c, m)
    local nc, na = cost[i] + step, acts[i] + 1
    if nc < cost[j] or (nc == cost[j] and na < acts[j]) then cost[j] = nc; acts[j] = na end
  end
  while true do
    local best = -1
    for i = 0, n - 1 do
      if not done[i] and cost[i] < math.huge and (best < 0 or cost[i] < cost[best]
          or (cost[i] == cost[best] and acts[i] < acts[best])) then
        best = i
      end
    end
    if best < 0 then return { cost = -1, actions = -1 } end
    done[best] = true
    local m = best % 4
    local cellIndex = (best - m) / 4
    local c = cellIndex % cols + 1
    local r = (cellIndex - (c - 1)) / cols + 1
    if at(r, c) == 'G' then return { cost = cost[best], actions = acts[best] } end
    local moves = { { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 } }
    for dir, mv in ipairs(moves) do
      local nr, nc = r + mv[1], c + mv[2]
      if nr >= 1 and nr <= rows and nc >= 1 and nc <= cols then
        relax(best, nr, nc, m, at(nr, nc) == '~' and 3 or 1, dir)
      end
    end
    for _, t in ipairs(teleporters) do
      if t.fr == r and t.fc == c then relax(best, t.tr, t.tc, m, t.cost) end
    end
  end
end)
";

        [Test]
        public void DungeonRoute_GoldenSolution_ScoresFullBase()
        {
            ScenarioGrading grading = GradeWith(GameReasoningScenariosG3.All(), "g3_extreme_dungeon_route",
                RouteGolden);

            AssertFullBase(grading);
            Assert.Greater(grading.Bonus, 0);
        }

        [Test]
        public void DungeonRoute_PromptExamples_MatchTheReference()
        {
            ScenarioGrading grading = GradeWith(GameReasoningScenariosG3.All(), "g3_extreme_dungeon_route",
                RouteGolden + @"
local function check(grid, tele, cost, actions, label)
  local r = solve(grid, tele)
  assert(r.cost == cost and r.actions == actions, label .. ': ' .. r.cost .. '/' .. r.actions)
end
check({ 'S~G', '...' }, {}, 4, 2, 'example 1')
check({ 'S.A.G', '#.###', 'a....' }, {}, 10, 10, 'example 2')
check({ 'S#G', '.#.', '...' }, { { fr = 1, fc = 1, tr = 1, tc = 3, cost = 5 } }, 5, 1, 'example 3a')
check({ 'S#G', '.#.', '...' }, { { fr = 1, fc = 1, tr = 1, tc = 3, cost = 7 } }, 6, 6, 'example 3b')
check({ 'S<G', '...' }, {}, 4, 4, 'example 4')
");

            AssertFullBase(grading);
        }

        [Test]
        public void DungeonRoute_NaiveFullScanDijkstra_FitsTheInstructionBudget()
        {
            ScenarioGrading grading = GradeWith(GameReasoningScenariosG3.All(), "g3_extreme_dungeon_route",
                RouteNaiveFullScan);

            AssertFullBase(grading);
        }

        [Test]
        public void DungeonRoute_CostOnlySearch_LosesFewestActionsOnly()
        {
            ScenarioGrading grading = GradeWith(GameReasoningScenariosG3.All(), "g3_extreme_dungeon_route",
                Mutate(Mutate(RouteGolden,
                        "if cost[a] < cost[b] or (cost[a] == cost[b] and acts[a] < acts[b]) then bi = i end",
                        "if cost[a] < cost[b] then bi = i end"),
                    "elseif nc > cost[id] or (nc == cost[id] and na >= acts[id]) then",
                    "elseif nc >= cost[id] then"));

            AssertCheckpoint(grading, "fewest_actions", false);
            foreach (string id in new[]
                     {
                         "terrain_only", "keys_doors", "teleporters", "combined", "one_way_cells", "unreachable"
                     })
            {
                AssertCheckpoint(grading, id, true);
            }
        }

        [Test]
        public void DungeonRoute_KeysIgnoredInTheState_LosesKeyFamilies()
        {
            ScenarioGrading grading = GradeWith(GameReasoningScenariosG3.All(), "g3_extreme_dungeon_route",
                Mutate(RouteGolden, "local id = ((r - 1) * cols + c - 1) * 4 + mask",
                    "local id = ((r - 1) * cols + c - 1) * 4"));

            AssertCheckpoint(grading, "keys_doors", false);
            AssertCheckpoint(grading, "terrain_only", true);
            AssertCheckpoint(grading, "teleporters", true);
            AssertPartialBase(grading);
        }

        [Test]
        public void DungeonRoute_TeleportPaysDestinationEntry_LosesTeleporterFamilies()
        {
            ScenarioGrading grading = GradeWith(GameReasoningScenariosG3.All(), "g3_extreme_dungeon_route",
                Mutate(RouteGolden, "relax(id, t.tr, t.tc, mask, t.cost)",
                    "relax(id, t.tr, t.tc, mask, t.cost + (cell[t.tr][t.tc] == '~' and 3 or 1))"));

            AssertCheckpoint(grading, "teleporters", false);
            AssertCheckpoint(grading, "terrain_only", true);
            AssertCheckpoint(grading, "keys_doors", true);
            AssertPartialBase(grading);
        }

        [Test]
        public void DungeonRoute_ArrowsTreatedAsFloor_LosesOneWayFamily()
        {
            ScenarioGrading grading = GradeWith(GameReasoningScenariosG3.All(), "g3_extreme_dungeon_route",
                Mutate(RouteGolden, "    if move and ARROW[ch] and ARROW[ch] ~= move then return end\n", ""));

            AssertCheckpoint(grading, "one_way_cells", false);
            foreach (string id in new[] { "terrain_only", "keys_doors", "teleporters", "combined" })
            {
                AssertCheckpoint(grading, id, true);
            }
        }

        [Test]
        public void DungeonRoute_TeleportsBlockedByArrows_LosesOneWayFamily()
        {
            ScenarioGrading grading = GradeWith(GameReasoningScenariosG3.All(), "g3_extreme_dungeon_route",
                Mutate(RouteGolden, "    if move and ARROW[ch] and ARROW[ch] ~= move then return end\n",
                    "    if ARROW[ch] and ARROW[ch] ~= move then return end\n"));

            AssertCheckpoint(grading, "one_way_cells", false);
            AssertCheckpoint(grading, "teleporters", true);
            AssertCheckpoint(grading, "unreachable", true);
        }

        [Test]
        public void DungeonRoute_StepCountSearch_LosesTerrain()
        {
            ScenarioGrading grading = GradeWith(GameReasoningScenariosG3.All(), "g3_extreme_dungeon_route",
                Mutate(RouteGolden,
                    "if cost[a] < cost[b] or (cost[a] == cost[b] and acts[a] < acts[b]) then bi = i end",
                    "if acts[a] < acts[b] then bi = i end"));

            AssertCheckpoint(grading, "terrain_only", false);
            AssertPartialBase(grading);
        }

        [Test]
        public void DungeonRoute_HardcodedExamples_FailsEveryHiddenFamily()
        {
            ScenarioGrading grading = GradeWith(GameReasoningScenariosG3.All(), "g3_extreme_dungeon_route", @"
logic_define('route', function(grid, teleporters)
  if grid[1] == 'S~G' then return { cost = 4, actions = 2 } end
  if grid[1] == 'S.A.G' then return { cost = 10, actions = 10 } end
  if grid[1] == 'S#G' then return { cost = 5, actions = 1 } end
  return { cost = 2, actions = 2 }
end)");

            foreach (string id in new[]
                     {
                         "terrain_only", "keys_doors", "teleporters", "combined", "one_way_cells", "unreachable",
                         "fewest_actions"
                     })
            {
                AssertCheckpoint(grading, id, false);
            }

            Assert.Less(Base(grading), 25);
        }

        // ---------------------------------------------------------------------------------------------
        //  G4 extreme: auto-battler
        // ---------------------------------------------------------------------------------------------

        private const string BattlerGolden = @"
local S = { units = {}, list = {}, order = {}, pos = 1, round = 0, result = 'running' }
local MAX_ROUNDS = 20
local function alive(u) return u.hp > 0 end
local function die(t) t.hp = 0; t.shield = 0; t.poison = 0; t.stun = 0 end
local function hit(u, t)
  local dmg = math.max(1, u.atk - t.def)
  local a = math.min(t.shield, dmg)
  t.shield = t.shield - a
  t.hp = t.hp - (dmg - a)
  if t.hp <= 0 then die(t) end
end
local function enemyTarget(u)
  local best
  for _, x in ipairs(S.list) do
    if x.team ~= u.team and alive(x) then
      if best == nil then
        best = x
      else
        local ex, eb = x.hp + x.shield, best.hp + best.shield
        if ex < eb or (ex == eb and (x.hp < best.hp or (x.hp == best.hp and x.id < best.id))) then best = x end
      end
    end
  end
  return best
end
local function bestAlly(u, better)
  local best
  for _, x in ipairs(S.list) do
    if x.team == u.team and alive(x) and (best == nil or better(x, best)) then best = x end
  end
  return best
end
local function turn(u)
  if u.poison > 0 then
    u.hp = u.hp - u.poison
    u.poison = u.poison - 1
    if u.hp <= 0 then die(u); return end
  end
  if u.cooldown > 0 then u.cooldown = u.cooldown - 1 end
  if u.stun > 0 then u.stun = u.stun - 1; return end
  if u.skill == 'none' or u.cooldown > 0 then hit(u, enemyTarget(u)); return end
  if u.skill == 'poison' then
    local t = enemyTarget(u)
    hit(u, t)
    if alive(t) then t.poison = math.min(6, t.poison + 2) end
  elseif u.skill == 'stun' then
    local t = enemyTarget(u)
    hit(u, t)
    if alive(t) then t.stun = 1 end
  elseif u.skill == 'cleave' then
    local targets = {}
    for _, x in ipairs(S.list) do
      if x.team ~= u.team and alive(x) then targets[#targets + 1] = x end
    end
    for _, t in ipairs(targets) do hit(u, t) end
  elseif u.skill == 'shield' then
    local t = bestAlly(u, function(x, b) return x.hp < b.hp or (x.hp == b.hp and x.id < b.id) end)
    t.shield = math.min(20, t.shield + 2 * u.atk)
  elseif u.skill == 'heal' then
    local t = bestAlly(u, function(x, b)
      local mx, mb = x.max_hp - x.hp, b.max_hp - b.hp
      return mx > mb or (mx == mb and x.id < b.id)
    end)
    t.hp = math.min(t.max_hp, t.hp + 2 * u.atk)
  end
  u.cooldown = u.cd
end
local function teamAlive(team)
  for _, x in ipairs(S.list) do
    if x.team == team and alive(x) then return true end
  end
  return false
end
local function initiative(a, b)
  if a.spd ~= b.spd then return a.spd > b.spd end
  if a.hp ~= b.hp then return a.hp > b.hp end
  return a.id < b.id
end
logic_define('battle_start', function(teamA, teamB)
  S = { units = {}, list = {}, order = {}, pos = 1, round = 0, result = 'running' }
  for _, side in ipairs({ { 'A', teamA }, { 'B', teamB } }) do
    for _, src in ipairs(side[2]) do
      local u = { id = src.id, team = side[1], hp = src.hp, max_hp = src.hp, atk = src.atk, def = src.def,
        spd = src.spd, skill = src.skill, cd = src.cd, shield = 0, poison = 0, stun = 0, cooldown = 0 }
      S.units[u.id] = u
      S.list[#S.list + 1] = u
    end
  end
  return true
end)
logic_define('battle_step', function()
  if S.result ~= 'running' then return 'end' end
  local u
  while u == nil do
    while S.pos <= #S.order and not alive(S.order[S.pos]) do S.pos = S.pos + 1 end
    if S.pos <= #S.order then
      u = S.order[S.pos]
      S.pos = S.pos + 1
    elseif S.round == MAX_ROUNDS then
      S.result = 'draw'
      return 'end'
    else
      S.round = S.round + 1
      S.order = {}
      for _, x in ipairs(S.list) do
        if alive(x) then S.order[#S.order + 1] = x end
      end
      table.sort(S.order, initiative)
      S.pos = 1
    end
  end
  turn(u)
  if not teamAlive('A') then S.result = 'B' elseif not teamAlive('B') then S.result = 'A' end
  return u.id
end)
logic_define('unit_state', function(id)
  local u = S.units[id]
  return { hp = u.hp, shield = u.shield, poison = u.poison, stun = u.stun, cooldown = u.cooldown }
end)
logic_define('battle_result', function() return S.result end)
";

        [Test]
        public void AutoBattler_GoldenSolution_ScoresFullBase()
        {
            ScenarioGrading grading = GradeWith(GamePlaythroughScenariosG4.All(), "g4_extreme_auto_battler",
                BattlerGolden);

            AssertFullBase(grading);
            Assert.Greater(grading.Bonus, 0);
        }

        [Test]
        public void AutoBattler_PromptExample_MatchesTheReference()
        {
            GamePlaythroughScenariosG4.BattleCase example =
                GamePlaythroughScenariosG4.BattleCase.Of("A1:20,6,1,5,poison,2 vs B1:18,5,2,5,stun,3");
            List<GamePlaythroughScenariosG4.BattleStep> trace = GamePlaythroughScenariosG4.BattleReference.Run(example);
            string[] actors = { "A1", "B1", "A1", "B1", "A1" };
            for (int i = 0; i < actors.Length; i++)
            {
                Assert.AreEqual(actors[i], trace[i].Actor, $"call {i + 1}");
            }

            Assert.AreEqual(14, trace[0].After["B1"].Hp);
            Assert.AreEqual(2, trace[0].After["B1"].Poison);
            Assert.AreEqual(2, trace[0].After["A1"].Cooldown);
            Assert.AreEqual(12, trace[1].After["B1"].Hp);
            Assert.AreEqual(1, trace[1].After["B1"].Poison);
            Assert.AreEqual(16, trace[1].After["A1"].Hp);
            Assert.AreEqual(1, trace[1].After["A1"].Stun);
            Assert.AreEqual(3, trace[1].After["B1"].Cooldown);
            Assert.AreEqual(1, trace[2].After["A1"].Cooldown);
            Assert.AreEqual(0, trace[2].After["A1"].Stun);
            Assert.AreEqual(11, trace[3].After["B1"].Hp);
            Assert.AreEqual(0, trace[3].After["B1"].Poison);
            Assert.AreEqual(2, trace[3].After["B1"].Cooldown);
            Assert.AreEqual(12, trace[3].After["A1"].Hp);
            Assert.AreEqual(7, trace[4].After["B1"].Hp);
            Assert.AreEqual(2, trace[4].After["B1"].Poison);
            Assert.AreEqual(2, trace[4].After["A1"].Cooldown);
        }

        [Test]
        public void AutoBattler_StunCheckedBeforeCooldown_LosesStunFamilies()
        {
            ScenarioGrading grading = GradeWith(GamePlaythroughScenariosG4.All(), "g4_extreme_auto_battler",
                Mutate(BattlerGolden,
                    "  if u.cooldown > 0 then u.cooldown = u.cooldown - 1 end\n" +
                    "  if u.stun > 0 then u.stun = u.stun - 1; return end\n",
                    "  if u.stun > 0 then u.stun = u.stun - 1; return end\n" +
                    "  if u.cooldown > 0 then u.cooldown = u.cooldown - 1 end\n"));

            AssertCheckpoint(grading, "poison_stun", false);
            AssertCheckpoint(grading, "basic_combat", true);
            AssertCheckpoint(grading, "support_skills", true);
            AssertCheckpoint(grading, "initiative_order", true);
            AssertPartialBase(grading);
        }

        [Test]
        public void AutoBattler_InitiativeIgnoresHp_LosesInitiativeOrder()
        {
            ScenarioGrading grading = GradeWith(GamePlaythroughScenariosG4.All(), "g4_extreme_auto_battler",
                Mutate(BattlerGolden, "  if a.hp ~= b.hp then return a.hp > b.hp end\n", ""));

            AssertCheckpoint(grading, "initiative_order", false);
            AssertCheckpoint(grading, "basic_combat", false);
            AssertPartialBase(grading);
        }

        [Test]
        public void AutoBattler_OneRoundTooMany_LosesDrawRuleOnly()
        {
            ScenarioGrading grading = GradeWith(GamePlaythroughScenariosG4.All(), "g4_extreme_auto_battler",
                Mutate(BattlerGolden, "local MAX_ROUNDS = 20", "local MAX_ROUNDS = 21"));

            AssertCheckpoint(grading, "draw_rule", false);
            foreach (string id in new[]
                     {
                         "start_and_query", "basic_combat", "poison_stun", "support_skills", "mixed_battles",
                         "mixed_prefix", "initiative_order"
                     })
            {
                AssertCheckpoint(grading, id, true);
            }
        }

        [Test]
        public void AutoBattler_StartWithoutReturnValue_LosesOnlyTheStartCheckpoint()
        {
            ScenarioGrading grading = GradeWith(GamePlaythroughScenariosG4.All(), "g4_extreme_auto_battler",
                Mutate(BattlerGolden, "  return true\nend)\nlogic_define('battle_step'",
                    "end)\nlogic_define('battle_step'"));

            AssertCheckpoint(grading, "start_and_query", false);
            foreach (string id in new[]
                     {
                         "basic_combat", "poison_stun", "support_skills", "draw_rule", "mixed_battles",
                         "mixed_prefix", "initiative_order", "winners"
                     })
            {
                AssertCheckpoint(grading, id, true);
            }
        }

        [Test]
        public void AutoBattler_ShieldIgnoredByTargeting_LosesSupportFamilies()
        {
            ScenarioGrading grading = GradeWith(GamePlaythroughScenariosG4.All(), "g4_extreme_auto_battler",
                Mutate(BattlerGolden, "local ex, eb = x.hp + x.shield, best.hp + best.shield",
                    "local ex, eb = x.hp, best.hp"));

            AssertCheckpoint(grading, "support_skills", false);
            AssertCheckpoint(grading, "basic_combat", true);
            AssertCheckpoint(grading, "poison_stun", true);
        }

        [Test]
        public void AutoBattler_ExampleOnlyStub_FailsEveryHiddenFamily()
        {
            ScenarioGrading grading = GradeWith(GamePlaythroughScenariosG4.All(), "g4_extreme_auto_battler", @"
local calls = 0
local order = { 'A1', 'B1', 'A1', 'B1', 'A1' }
logic_define('battle_start', function() calls = 0; return true end)
logic_define('battle_step', function()
  calls = calls + 1
  return order[calls] or 'end'
end)
logic_define('unit_state', function(id) return { hp = 20, shield = 0, poison = 0, stun = 0, cooldown = 0 } end)
logic_define('battle_result', function() return 'running' end)");

            foreach (string id in new[]
                     {
                         "basic_combat", "poison_stun", "support_skills", "draw_rule", "mixed_battles",
                         "mixed_prefix", "initiative_order", "winners"
                     })
            {
                AssertCheckpoint(grading, id, false);
            }

            Assert.Less(Base(grading), 25);
        }

        // ---------------------------------------------------------------------------------------------
        //  Tool hygiene
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void FailedLuaChunk_IsChargedOnceByTheCleanCheckpoint()
        {
            List<GameBenchmarkScenario> scenarios = new(GameMechanicScenariosG2.All());
            scenarios.Add(Array.Find(GameReasoningScenariosG3.All(), s => s.Id == "g3_extreme_dungeon_route"));
            scenarios.Add(Array.Find(GamePlaythroughScenariosG4.All(), s => s.Id == "g4_extreme_auto_battler"));
            foreach (GameBenchmarkScenario scenario in scenarios)
            {
                CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
                try
                {
                    BenchmarkEnvironment env = new(settings);
                    scenario.Prepare(env);
                    env.Lua.Seed("error('self-test went wrong')");
                    Assert.AreEqual(1, env.Lua.FailedExecutions, scenario.Id);
                    RunObservation run = new() { Turns = 2, ToolCalls = 2, FailedToolCalls = 1 };

                    ScenarioGrading grading = scenario.Grade(env, run);
                    GameCreationBenchmarkHarness.ApplyUnscoredToolErrorPenalties(grading, run);

                    BenchmarkCheckpoint clean = grading.Checkpoints.Find(c => c.Id.StartsWith("clean_"));
                    Assert.IsNotNull(clean, scenario.Id);
                    Assert.IsFalse(clean.Passed, scenario.Id);
                    Assert.IsTrue(grading.ScoresFailedToolCalls, scenario.Id);
                    Assert.AreEqual(0, grading.Penalties.Count, $"{scenario.Id} charged the failed call twice");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }
            }
        }

        [Test]
        public void ExtremeGoals_StateTheRunRules()
        {
            foreach (GameBenchmarkScenario scenario in new[]
                     {
                         Array.Find(GameMechanicScenariosG2.All(), s => s.Id == "g2_extreme_damage_pipeline"),
                         Array.Find(GameReasoningScenariosG3.All(), s => s.Id == "g3_extreme_dungeon_route"),
                         Array.Find(GamePlaythroughScenariosG4.All(), s => s.Id == "g4_extreme_auto_battler")
                     })
            {
                Assert.IsTrue(scenario.IsExtreme, scenario.Id);
                StringAssert.Contains("You have about 8 minutes for this task.", scenario.Goal, scenario.Id);
                StringAssert.Contains("One slot call may run at most 200,000 Lua VM instructions; a call that " +
                                      "exceeds it or raises uninstalls the slot", scenario.Goal, scenario.Id);
                StringAssert.Contains("check values by returning a string, never by raising", scenario.Goal,
                    scenario.Id);
                StringAssert.Contains("at most 20 calls per 60 seconds", scenario.Goal, scenario.Id);
                StringAssert.DoesNotContain("assert(", scenario.Goal, scenario.Id);
            }
        }

        // ---------------------------------------------------------------------------------------------
        //  Hardened regular scenarios
        // ---------------------------------------------------------------------------------------------

        [Test]
        public void WinCondition_CorrectRulePasses_GreaterThan99Fails()
        {
            AssertAllPass(GradeWith(GameMechanicScenariosG2.All(), "g2_win_condition",
                "logic_define('win_condition', function(score) return score >= 100 end)"));

            ScenarioGrading loose = GradeWith(GameMechanicScenariosG2.All(), "g2_win_condition",
                "logic_define('win_condition', function(score) return score > 99 end)");
            AssertCheckpoint(loose, "hidden_samples", false);
        }

        [Test]
        public void MultiArgDamage_CorrectRulePasses_ClampOnlyAtZeroInputsFails()
        {
            AssertAllPass(GradeWith(GameMechanicScenariosG2.All(), "g2_multi_arg_damage",
                "logic_define('calculate_damage', function(base, level, armor) " +
                "return math.max(0, base + level * 10 - armor) end)"));

            ScenarioGrading probeShaped = GradeWith(GameMechanicScenariosG2.All(), "g2_multi_arg_damage",
                "logic_define('calculate_damage', function(base, level, armor) " +
                "if base == 0 and level == 0 then return 0 end return base + level * 10 - armor end)");
            AssertCheckpoint(probeShaped, "clamped", false);
        }

        [Test]
        public void CraftingRules_CorrectResolverPasses_AnyPlankMakesTableFails()
        {
            const string correct = "local R = { ['wood+wood'] = 'plank', ['plank+plank'] = 'table', " +
                                   "['iron+coal'] = 'steel', ['wood+coal'] = 'torch' } " +
                                   "logic_define('craft_result', function(a, b) " +
                                   "return R[a .. '+' .. b] or R[b .. '+' .. a] or 'none' end)";
            AssertAllPass(GradeWith(GameMechanicScenariosG2.All(), "g2_crafting_rules", correct));

            ScenarioGrading sloppy = GradeWith(GameMechanicScenariosG2.All(), "g2_crafting_rules",
                "logic_define('craft_result', function(a, b) " +
                "if a == 'plank' or b == 'plank' then return 'table' end " +
                "if a == 'coal' or b == 'coal' then if a == 'wood' or b == 'wood' then return 'torch' end " +
                "if a == 'iron' or b == 'iron' then return 'steel' end end " +
                "if a == 'wood' and b == 'wood' then return 'plank' end return 'none' end)");
            AssertCheckpoint(sloppy, "rejects_unknown", false);
            AssertCheckpoint(sloppy, "recipes", true);
        }

        [Test]
        public void TieredPricing_CorrectFormulaPasses_LookupTableUpTo30Fails()
        {
            AssertAllPass(GradeWith(GameReasoningScenariosG3.All(), "g3_tiered_pricing",
                "logic_define('price', function(qty) " +
                "if qty <= 10 then return qty * 5 end " +
                "if qty <= 20 then return 50 + (qty - 10) * 4 end " +
                "return 90 + (qty - 20) * 3 end)"));

            ScenarioGrading table = GradeWith(GameReasoningScenariosG3.All(), "g3_tiered_pricing",
                "local P = {} local t = 0 for q = 1, 30 do " +
                "if q <= 10 then t = t + 5 elseif q <= 20 then t = t + 4 else t = t + 3 end P[q] = t end " +
                "logic_define('price', function(qty) return P[qty] or 0 end)");
            AssertCheckpoint(table, "cross_tier", false);
        }

        [Test]
        public void ClampedRegen_CorrectClampPasses_HardcodedMax100Fails()
        {
            AssertAllPass(GradeWith(GameReasoningScenariosG3.All(), "g3_clamped_regen",
                "logic_define('regen', function(hp, amount, max_hp) " +
                "return math.max(0, math.min(max_hp, hp + amount)) end)"));

            ScenarioGrading hardcoded = GradeWith(GameReasoningScenariosG3.All(), "g3_clamped_regen",
                "logic_define('regen', function(hp, amount, max_hp) " +
                "return math.max(0, math.min(100, hp + amount)) end)");
            AssertCheckpoint(hardcoded, "clamp_high", false);
            AssertCheckpoint(hardcoded, "clamp_low", true);
        }

        [Test]
        public void FibonacciRewards_NaiveRecursionPasses_HardcodedTwelveFails()
        {
            AssertAllPass(GradeWith(GameReasoningScenariosG3.All(), "g3_fibonacci_rewards",
                "local function fib(n) if n <= 2 then return 1 end return fib(n - 1) + fib(n - 2) end " +
                "logic_define('wave_reward', fib)"));

            ScenarioGrading listed = GradeWith(GameReasoningScenariosG3.All(), "g3_fibonacci_rewards",
                "local F = { 1, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144 } " +
                "logic_define('wave_reward', function(n) return F[n] or 0 end)");
            AssertCheckpoint(listed, "derived", false);
        }

        [Test]
        public void BooleanDungeon_CorrectLogicPasses_XorFails()
        {
            string[] spawns = { "Player", "Key", "Door" };
            AssertAllPass(GradeWith(GameReasoningScenariosG3.All(), "g3_boolean_dungeon",
                "logic_define('can_win', function(k, d, b) return k and d and not b end)", spawns));

            ScenarioGrading xor = GradeWith(GameReasoningScenariosG3.All(), "g3_boolean_dungeon",
                "logic_define('can_win', function(k, d, b) return (k and d) ~= b end)", spawns);
            AssertCheckpoint(xor, "logic", false);
            AssertCheckpoint(xor, "win_case", true);
        }

        [Test]
        public void CombatPlaythrough_CorrectRulesPass_DeadOnlyAtZeroFails()
        {
            const string rules =
                "logic_define('attack_damage', function(p, a) return math.max(1, p - a) end) " +
                "logic_define('apply_damage', function(hp, d) return math.max(0, hp - d) end) ";
            AssertAllPass(GradeWith(GamePlaythroughScenariosG4.All(), "g4_combat",
                rules + "logic_define('is_dead', function(hp) return hp <= 0 end)"));

            ScenarioGrading zeroOnly = GradeWith(GamePlaythroughScenariosG4.All(), "g4_combat",
                rules + "logic_define('is_dead', function(hp) return hp == 0 end)");
            AssertCheckpoint(zeroOnly, "edges", false);
            AssertCheckpoint(zeroOnly, "simulated_fight", true);
            AssertCheckpoint(zeroOnly, "hidden_fights", true);

            ScenarioGrading tuned = GradeWith(GamePlaythroughScenariosG4.All(), "g4_combat",
                "logic_define('attack_damage', function(p, a) return 8 end) " +
                "logic_define('apply_damage', function(hp, d) return math.max(0, hp - d) end) " +
                "logic_define('is_dead', function(hp) return hp <= 0 end)");
            AssertCheckpoint(tuned, "simulated_fight", true);
            AssertCheckpoint(tuned, "hidden_fights", false);
        }

        [Test]
        public void ShopPlaythrough_CorrectRulesPass_StrictGreaterFails()
        {
            const string prices =
                "local P = { sword = 100, potion = 30, shield = 75 } " +
                "logic_define('item_price', function(item) return P[item] or 0 end) " +
                "logic_define('buy', function(gold, price) return gold - price end) ";
            AssertAllPass(GradeWith(GamePlaythroughScenariosG4.All(), "g4_shop",
                prices + "logic_define('can_buy', function(gold, price) return gold >= price end)"));

            ScenarioGrading strict = GradeWith(GamePlaythroughScenariosG4.All(), "g4_shop",
                prices + "logic_define('can_buy', function(gold, price) return gold > price end)");
            AssertCheckpoint(strict, "rules", false);
            AssertCheckpoint(strict, "hidden_session", false);
            AssertCheckpoint(strict, "simulated_session", true);
        }

        [Test]
        public void CraftingChain_CorrectResolverPasses_AnyPlankMakesTableFails()
        {
            AssertAllPass(GradeWith(GamePlaythroughScenariosG4.All(), "g4_crafting_chain",
                "local R = { ['wood+wood'] = 'plank', ['plank+plank'] = 'table', ['wood+stone'] = 'axe' } " +
                "logic_define('craft', function(a, b) return R[a .. '+' .. b] or R[b .. '+' .. a] or 'none' end)"));

            ScenarioGrading sloppy = GradeWith(GamePlaythroughScenariosG4.All(), "g4_crafting_chain",
                "logic_define('craft', function(a, b) " +
                "if a == 'plank' or b == 'plank' then return 'table' end " +
                "if a == 'stone' or b == 'stone' then return 'axe' end " +
                "if a == 'wood' and b == 'wood' then return 'plank' end return 'none' end)");
            AssertCheckpoint(sloppy, "reject_and_symmetry", false);
            AssertCheckpoint(sloppy, "simulated_chain", true);
        }

        // ---------------------------------------------------------------------------------------------
        //  Helpers
        // ---------------------------------------------------------------------------------------------

        private static ScenarioGrading GradeWith(GameBenchmarkScenario[] group, string scenarioId, string lua,
            string[] spawnedNames = null)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                GameBenchmarkScenario scenario = Array.Find(group, candidate => candidate.Id == scenarioId);
                Assert.IsNotNull(scenario, scenarioId);
                BenchmarkEnvironment env = new(settings);
                scenario.Prepare(env);
                env.Lua.Seed(lua);
                Assert.AreEqual(0, env.Lua.FailedExecutions, env.Lua.LastError);
                foreach (string name in spawnedNames ?? Array.Empty<string>())
                {
                    env.World.Commands.Add(new RecordedWorldCommand { Action = "spawn", TargetName = name });
                }

                return scenario.Grade(env, new RunObservation { Turns = 1, ToolCalls = 1 });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        private static string Mutate(string lua, string from, string to)
        {
            // WHY: a CRLF checkout turns the verbatim solutions' line breaks into \r\n, while the anchors use \n.
            string normalized = lua.Replace("\r\n", "\n");
            Assert.IsTrue(normalized.Contains(from), $"mutation anchor not found: {from}");
            return normalized.Replace(from, to);
        }

        private static double Base(ScenarioGrading grading)
        {
            return GoalScore.Compute(grading.Checkpoints, grading.Penalties, grading.Bonus, grading.HardCap).Base;
        }

        private static void AssertFullBase(ScenarioGrading grading)
        {
            AssertAllPass(grading);
            Assert.AreEqual(0, grading.Penalties.Count);
            Assert.AreEqual(100, Base(grading), 1e-9);
        }

        private static void AssertAllPass(ScenarioGrading grading)
        {
            foreach (BenchmarkCheckpoint checkpoint in grading.Checkpoints)
            {
                Assert.IsTrue(checkpoint.Passed, $"{checkpoint.Id}: {checkpoint.Detail}");
            }
        }

        private static void AssertPartialBase(ScenarioGrading grading)
        {
            double score = Base(grading);
            Assert.Greater(score, 20, "a single wrong rule should keep partial credit");
            Assert.Less(score, 95, "a wrong rule must cost points");
        }

        private static void AssertCheckpoint(ScenarioGrading grading, string id, bool expected)
        {
            BenchmarkCheckpoint checkpoint = grading.Checkpoints.Find(c => c.Id == id);
            Assert.IsNotNull(checkpoint, $"Missing checkpoint '{id}'.");
            Assert.AreEqual(expected, checkpoint.Passed, $"{id}: {checkpoint.Detail}");
        }
    }
}
#endif
#endif
