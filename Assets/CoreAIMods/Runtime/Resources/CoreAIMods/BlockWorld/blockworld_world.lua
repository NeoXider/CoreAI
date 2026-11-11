--[[@coreai
id: blockworld_world
name: Block World 1 - World
version: 1.3.2
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo (the eight mods of the Samples/Block World folder; turn the whole folder on). Generates a seeded voxel world in workspace.Terrain - islands and coasts with sandy beaches, grass lowlands, hills, rocky ridges and snowy peaks, lakes, woods and single trees of three shapes, winding caves with chambers and mouths that open onto hillsides (flooded below the sea level at the shore), and an unbreakable bedrock floor - and streams it around the player in budgeted steps: the spawn's column first, then what the camera faces. The spawn and the first heading are chosen so the first view shows a peak, water and a wood. The same seed always gives the same world; set the workspace attribute BlockWorldSeed before the mod loads to pick another. Generation only fills empty cells, so a saved build is never overwritten.
]]

-- How it works. The world is cut into columns of 16 x 32 x 16 cells (one Terrain chunk wide, two
-- chunks tall). A column is generated in phases, each small enough for one Heartbeat: sample the
-- surface heights, fill the deep stone with one ReplaceMaterial call, fill the pillars above it into
-- arrays from ReadVoxels, plant the trees, then write the arrays back with ONE WriteVoxels call and lay
-- the bedrock row last. The bedrock is the "this column is done" mark: a reload (or a saved world)
-- skips every column that has it. Pillar heights come from math.noise and trees from a Random seeded
-- per column, so the world does not depend on the order the player walks in. Nothing is generated while the
-- mod loads: a reload (an AI edit, a hot reload) runs under a tool's 100 ms limit, and a column takes longer.
--
-- A new player waits at the spawn, held, until the first view is built: the spawn's column and the next ones,
-- which are the columns the camera faces (BlockWorldOpened; the HUD draws its "Building the world" veil until then).
--
-- Other Block World mods read what this one publishes as workspace attributes:
--   BlockWorldSeed (number), BlockWorldReady (true once the spawn column exists),
--   BlockWorldOpened (false while a new player waits for the first view), BlockWorldOpeningColumns (how many
--   columns that view needs), BlockWorldSpawn (Vector3, studs), BlockWorldColumns (columns generated),
--   BlockWorldViewStuds (how far the streamed land is sure to reach from anywhere in the player's column, in
--   studs: the fog hides the world's edge there),
--   BlockWorldNotice (text).

local Players = game:GetService("Players")
local RunService = game:GetService("RunService")

local terrain = workspace.Terrain

local DEFAULT_SEED = 1337
local CELL = 4                  -- studs per cell (Terrain's grid)
local CHUNK = 16                -- cells per column side (one Terrain chunk)
local PILLARS = CHUNK * CHUNK
local BOTTOM = -16              -- bedrock row: the floor nobody digs through
local TOP = 16                  -- first row above every column
local PEAK = TOP - 3            -- the highest surface: snow on it still has room above
local SNOW_FROM = 12            -- a surface this high is snow
local ROCK_FROM = 9             -- a surface this high (or this steep: STEEP) is bare rock
local STEEP = 3                 -- rows to the lowest neighbour that make a surface a rock face
local WATER = -2                -- top row of the sea
local SHORE = WATER + 1         -- pillars this low get sand instead of grass
local VIEW_RADIUS = 4           -- columns streamed around the player
local HEIGHT_BUDGET = 32        -- surface heights one frame may sample (each reads five noises)
-- WHY 220: a cell costs about 16 instructions, so the fullest frame stays near 4,000 of the 5,000 (half the default
-- resume budget) the tests hold every streaming step to.
local PILLAR_BUDGET = 220       -- work one frame may spend filling pillars, in cells (see pillarWork)
local PILLAR_WORK = 12          -- a pillar's own cost, in cells: its materials, its slope, its mouth rows
local CAVE_WORK = 24            -- the most a pillar's caves cost, in cells: up to thirteen rows of 3D noise
local OPENING_COLUMNS = 4       -- columns made before a new player is let go: the spawn's and the three it faces
local OPENING_SECONDS = 6       -- the longest a new player waits for them
local PROBE_BUDGET = 1          -- "is this column already generated?" reads per frame (each scans the radius)
local LOOK_PULL = 0.5           -- columns a column straight ahead of the camera counts as nearer
local SPAWN_SEARCH = 24         -- rings of pillars searched for land around the middle of the first column
-- WHY the middle: a player standing there sees a whole column of ground around them at once, while the columns
-- beyond it are still being made.
local SPAWN_FROM = CHUNK / 2
local TREE_GRID = 3             -- a column offers one tree place per 3 x 3 cells (4 x 4 places)
local WOOD = 0                  -- forest noise above this is a wood (about half of the land)
local WOOD_CHANCE = 0.9         -- a place in a wood grows a tree this often
local FIELD_CHANCE = 0.1        -- a place in the open, this often
-- WHY a cost and not a count: a tall tree is a quarter of a big one; a frame grows trees until about 3,000 of its
-- instructions are spent (a big oak alone is about 2,400), and planning them takes a frame of its own.
local TREE_FRAME_COST = 3000
local TREE_COST = { oak = 1700, tall = 600, big = 2400 }
local MOUTH_CHANCE = 0.85       -- a column with a hillside gets a cave mouth this often
local MOUTH_RISE = 3            -- rows the ground climbs in four cells for a hillside
local MOUTH_LENGTH = 8          -- cells a mouth's tunnel runs into the hill
local FALL_FLOOR = BOTTOM * CELL - 24
local CAVE_HALF = 2             -- rows above and below a tunnel's centre it may wind through
local CHAMBER_HALF = 3          -- the same where a chamber opens
local TUNNEL = 0.12             -- |3D noise| below this is tunnel: one to three cells wide
local CHAMBER = 0.3             -- the same inside a chamber
local SPAWN_CLEAR = 4           -- cells around the spawn pillar that keep solid ground

local AIR = Enum.Material.Air
local BEDROCK = Enum.Material.Basalt
local STONE = Enum.Material.Rock
local DIRT = Enum.Material.Ground
local GRASS = Enum.Material.Grass
local SAND = Enum.Material.Sand
local SNOW = Enum.Material.Snow
local SEA = Enum.Material.Water
local TRUNK = Enum.Material.Mud -- WHY: Terrain has no log material; the block look draws Mud as bark
local LEAVES = Enum.Material.LeafyGrass

local seed = workspace:GetAttribute("BlockWorldSeed")
if type(seed) ~= "number" then
    seed = DEFAULT_SEED
end
seed = math.floor(seed)
workspace:SetAttribute("BlockWorldSeed", seed)

-- WHY a knob: a WebGL or low-end host streams a smaller world (each column is two Terrain chunks).
local viewRadius = workspace:GetAttribute("BlockWorldViewRadius")
viewRadius = type(viewRadius) == "number" and math.clamp(math.floor(viewRadius), 1, 8) or VIEW_RADIUS

local seedRandom = Random.new(seed)
local OFFSET_X = seedRandom:NextNumber() * 4096 + 0.5
local OFFSET_Z = seedRandom:NextNumber() * 4096 + 0.5

local noise = math.noise

-- The top solid row of the pillar at cell (x, z). Three layers, each with a job: a continent noise (two octaves)
-- says sea, lowland or highland; hills (two octaves) roll the land and only ripple the sea floor; ridges, 1 - |noise|
-- squared, grow mountains only in the highlands, so peaks come in ranges with valleys between them.
local function surfaceHeight(x, z)
    local continent = noise((x + OFFSET_X) / 80, (z + OFFSET_Z) / 80, 0.25)
        + 0.5 * noise((x - OFFSET_Z) / 37, (z + OFFSET_X) / 37, 1.75)
    local hills = noise((x + OFFSET_Z) / 24, (z + OFFSET_X) / 24, 0.75)
        + 0.5 * noise((x - OFFSET_X) / 11, (z - OFFSET_Z) / 11, 1.25)
    local ridge = 1 - math.abs(noise((x - OFFSET_Z) / 32, (z + OFFSET_X) / 32, 3.75))
    local t = math.clamp((continent + 0.35) / 0.4, 0, 1)
    local land = t * t * (3 - 2 * t)
    t = math.clamp((continent - 0.1) / 0.4, 0, 1)
    local high = t * t * (3 - 2 * t)
    local height = -8 + land * 10 + hills * (1 + 3 * land) + ridge * ridge * 12 * high
    return math.clamp(math.round(height), BOTTOM + 4, PEAK)
end

-- How much of a wood the place (x, z) is in: above WOOD it is a wood, below it open ground.
local function forest(x, z)
    return noise((x + OFFSET_X) / 37, (z - OFFSET_Z) / 37, 5.5)
end

local function columnRegion(cx, cz, y0, y1)
    local x, z = cx * CHUNK * CELL, cz * CHUNK * CELL
    return Region3.new(Vector3.new(x, y0 * CELL, z), Vector3.new(x + CHUNK * CELL, (y1 + 1) * CELL, z + CHUNK * CELL))
end

local function columnKey(cx, cz)
    return (cx + 32768) * 65536 + (cz + 32768)
end

local function columnRandom(cx, cz)
    return Random.new(seed * 31 + (cx + 8192) * 92821 + (cz + 8192) * 68917)
end

local function isGenerated(cx, cz)
    local x, z = cx * CHUNK * CELL, cz * CHUNK * CELL
    local probe = Region3.new(Vector3.new(x, BOTTOM * CELL, z), Vector3.new(x + CELL, (BOTTOM + 1) * CELL, z + CELL))
    local materials = terrain:ReadVoxels(probe, 4)
    return materials[1][1][1] == BEDROCK
end

-- ---- caves -------------------------------------------------------------------------------------
-- A pillar's caves lie in a band of rows 5 to 9 under its surface (a slow 2D noise sets the depth, so tunnels
-- rise and fall gently from pillar to pillar); inside the band, cells where a 3D noise is near zero are tunnel,
-- and that surface of zeros is continuous, so tunnels wind and join. Where the chamber noise is high the band and
-- the tunnel widen into a chamber. The band keeps a roof, so the way in is a mouth: where the ground climbs a
-- hillside, a two-cell-high tunnel runs from its foot into the hill and ends in a small chamber (see planMouth).
-- The rock stays closed under the sea and the shore, the row above bedrock stays, and a cave at the shore or under
-- the sea is flooded up to the sea level; inland caves stay dry.

local spawnCellX, spawnCellZ = 0, 0   -- set once the spawn is chosen; the ground around it stays whole
local spawnColumnKeyX, spawnColumnKeyZ = 0, 0   -- the spawn's column has no caves, so it is ready soonest

-- The rows of pillar (x, z) that are cave, as a set; nil when it has none.
local function caveRows(x, z, h)
    if math.abs(x - spawnCellX) <= SPAWN_CLEAR and math.abs(z - spawnCellZ) <= SPAWN_CLEAR then
        return nil
    end
    if math.floor(x / CHUNK) == spawnColumnKeyX and math.floor(z / CHUNK) == spawnColumnKeyZ then
        return nil
    end
    local depth = 5 + math.floor((noise((x + OFFSET_Z) / 47, (z - OFFSET_X) / 47, 2.5) + 1) * 2)
    local centre = h - depth
    local chamber = noise((x - OFFSET_X) / 17, (z + OFFSET_Z) / 17, 4.5) > 0.45
    local half = chamber and CHAMBER_HALF or CAVE_HALF
    local width = chamber and CHAMBER or TUNNEL
    local top = centre + half
    if h <= SHORE + 1 then
        top = math.min(top, h - 4)
    end
    local rows = nil
    for y = math.max(centre - half, BOTTOM + 2), top do
        local value = noise((x + OFFSET_X) / 15, y / 8 + 0.37, (z + OFFSET_Z) / 15)
        if value < width and value > -width then
            rows = rows or {}
            rows[y] = true
        end
    end
    return rows
end

-- ---- one column, phase by phase ------------------------------------------------------------------

local function newJob(cx, cz)
    return {
        cx = cx, cz = cz, x0 = cx * CHUNK, z0 = cz * CHUNK,
        phase = "heights", next = 1, heights = {}, minHeight = TOP, mouth = {},
    }
end

local DIRECTIONS = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } }

-- A hillside cave mouth for the column, from its heights alone: the foot of the steepest climb (MOUTH_RISE rows in
-- four cells) and a tunnel two cells high and two wide from the foot into the hill, ending in a chamber. The rows
-- go into job.mouth[pillar] = { from, to }, which fillPillar leaves empty. Nothing in the spawn's column.
local function planMouth(job)
    if not job.caves or columnRandom(job.cx + 4096, job.cz):NextNumber() > MOUTH_CHANCE then
        return
    end
    local heights = job.heights
    local best, bestRise = nil, MOUTH_RISE - 1
    for lz = 3, 14, 2 do
        for lx = 3, 14, 2 do
            local foot = heights[(lz - 1) * CHUNK + lx]
            if foot > SHORE + 1 then
                for _, d in ipairs(DIRECTIONS) do
                    local ex, ez = lx + d[1] * 4, lz + d[2] * 4
                    if ex >= 2 and ex <= CHUNK - 1 and ez >= 2 and ez <= CHUNK - 1 then
                        local rise = heights[(ez - 1) * CHUNK + ex] - foot
                        if rise > bestRise then
                            best, bestRise = { lx = lx, lz = lz, foot = foot, dx = d[1], dz = d[2] }, rise
                        end
                    end
                end
            end
        end
    end
    if not best then
        return
    end
    local function carve(lx, lz, from, to)
        if lx >= 1 and lx <= CHUNK and lz >= 1 and lz <= CHUNK then
            local index = (lz - 1) * CHUNK + lx
            local rows = job.mouth[index]
            job.mouth[index] = rows and { math.min(rows[1], from), math.max(rows[2], to) } or { from, to }
        end
    end
    local floor = best.foot + 1
    local lx, lz = best.lx, best.lz
    for _ = 1, MOUTH_LENGTH do
        lx, lz = lx + best.dx, lz + best.dz
        if lx < 2 or lx > CHUNK - 1 or lz < 2 or lz > CHUNK - 1 then
            break
        end
        carve(lx, lz, floor, floor + 1)
        carve(lx + best.dz, lz + best.dx, floor, floor + 1)
    end
    for dz = -1, 1 do
        for dx = -1, 1 do
            carve(lx + dx, lz + dz, floor, floor + 2)
        end
    end
    job.hasMouth = true
end

-- Fills rows y0..y1 of one pillar with `material`, leaving every cell that is already occupied; a cave row (or a
-- mouth row, mouthFrom..mouthTo) stays empty, or holds water at or below the sea level where the pillar is at the
-- shore or under the sea.
local function fillRun(materialColumn, occupancyColumn, lz, y0, y1, material, rowBase, cave, flooded, mouthFrom,
    mouthTo)
    for y = y0, y1 do
        local row = y - rowBase
        local occupancyRow = occupancyColumn[row]
        if occupancyRow[lz] == 0 then
            if (y < mouthFrom or y > mouthTo) and not (cave and cave[y]) then
                occupancyRow[lz] = 1
                materialColumn[row][lz] = material
            elseif flooded and y <= WATER then
                occupancyRow[lz] = 1
                materialColumn[row][lz] = SEA
            end
        end
    end
end

-- The work fillPillar(job, index) takes at most, in cells. It is known before the pillar is filled, so a frame
-- stops before the pillar that would take it over its budget.
local function pillarWork(job, index)
    return math.max(job.heights[index], WATER) - job.bandBottom + 1 + PILLAR_WORK + (job.caves and CAVE_WORK or 0)
end

-- The rows to the lowest of the four neighbours inside the column (a rock face at STEEP or more).
local function drop(heights, lx, lz, h)
    local lowest = h
    if lx > 1 then lowest = math.min(lowest, heights[(lz - 1) * CHUNK + lx - 1]) end
    if lx < CHUNK then lowest = math.min(lowest, heights[(lz - 1) * CHUNK + lx + 1]) end
    if lz > 1 then lowest = math.min(lowest, heights[(lz - 2) * CHUNK + lx]) end
    if lz < CHUNK then lowest = math.min(lowest, heights[lz * CHUNK + lx]) end
    return h - lowest
end

-- The top cell and the three under it: sand at and below the shore, snow on the peaks over rock, bare rock high up
-- and on faces too steep to hold soil, grass over earth everywhere else (the grass block shows earth on its sides,
-- so a stepped hillside reads as Minecraft's).
local function surfaceMaterials(h, steep)
    if h <= SHORE then
        return SAND, SAND
    elseif h >= SNOW_FROM then
        return SNOW, STONE
    elseif h >= ROCK_FROM or steep >= STEEP then
        return STONE, STONE
    end
    return GRASS, DIRT
end

local function fillPillar(job, index)
    local lx = (index - 1) % CHUNK + 1
    local lz = math.floor((index - 1) / CHUNK) + 1
    local h = job.heights[index]
    local materialColumn, occupancyColumn = job.materials[lx], job.occupancy[lx]
    local base = job.bandBottom
    local rowBase = base - 1
    local top, under = surfaceMaterials(h, drop(job.heights, lx, lz, h))
    local cave = job.caves and caveRows(job.x0 + lx - 1, job.z0 + lz - 1, h) or nil
    local flooded = h <= SHORE + 2
    local mouth = job.mouth[index]
    local mouthFrom, mouthTo = 1, 0
    if mouth then
        mouthFrom, mouthTo = mouth[1], mouth[2]
    end
    fillRun(materialColumn, occupancyColumn, lz, base, h - 4, STONE, rowBase, cave, flooded, mouthFrom, mouthTo)
    fillRun(materialColumn, occupancyColumn, lz, math.max(base, h - 3), h - 1, under, rowBase, cave, flooded, mouthFrom,
        mouthTo)
    fillRun(materialColumn, occupancyColumn, lz, h, h, top, rowBase, cave, flooded, mouthFrom, mouthTo)
    fillRun(materialColumn, occupancyColumn, lz, h + 1, WATER, SEA, rowBase, nil, false, 1, 0)
end

local function setCell(job, lx, y, lz, material)
    local row = y - job.bandBottom + 1
    local occupancyRow = job.occupancy[lx][row]
    if occupancyRow[lz] == 0 then
        occupancyRow[lz] = 1
        job.materials[lx][row][lz] = material
    end
end

-- A canopy layer of `radius` around (lx, lz) at row y: the corners stay open, and each edge leaf is kept or not by
-- the tree's own dice (`cut` in 0..1), so no two crowns look stamped.
local function canopyLayer(job, lx, y, lz, radius, random, cut)
    for dz = -radius, radius do
        local edgeZ = dz == radius or dz == -radius
        for dx = -radius, radius do
            local edgeX = dx == radius or dx == -radius
            if not (edgeX and edgeZ) and (radius < 2 or not (edgeX or edgeZ) or random:NextNumber() > cut) then
                setCell(job, lx + dx, y, lz + dz, LEAVES)
            end
        end
    end
end

-- Three shapes: a round oak (trunk 4-5, a crown two wide), a tall narrow tree (trunk 6-7, a crown one wide) and a
-- big oak (trunk 6, a deep crown). Leaves fill only empty cells, so trees in a wood grow into one canopy.
local function growTree(job, tree, random)
    local top = tree.ground + tree.trunk
    for y = tree.ground + 1, top do
        setCell(job, tree.lx, y, tree.lz, TRUNK)
    end
    if tree.kind == "tall" then
        for y = top - 2, top do
            canopyLayer(job, tree.lx, y, tree.lz, 1, random, 0)
        end
    elseif tree.kind == "big" then
        canopyLayer(job, tree.lx, top - 2, tree.lz, 2, random, 0.5)
        canopyLayer(job, tree.lx, top - 1, tree.lz, 2, random, 0.2)
        canopyLayer(job, tree.lx, top, tree.lz, 2, random, 0.35)
        canopyLayer(job, tree.lx, top + 1, tree.lz, 1, random, 0)
    else
        canopyLayer(job, tree.lx, top - 1, tree.lz, 2, random, 0.35)
        canopyLayer(job, tree.lx, top, tree.lz, 2, random, 0.5)
        canopyLayer(job, tree.lx, top + 1, tree.lz, 1, random, 0)
    end
    setCell(job, tree.lx, top + 2, tree.lz, LEAVES)
end

-- Chooses this column's trees without writing anything: one place per TREE_GRID cells, jittered, that grows a tree
-- WOOD_CHANCE of the time inside a wood and FIELD_CHANCE in the open; never on sand, rock or snow, on a cave mouth,
-- next to another trunk or within two cells of the spawn, and never so tall that its crown leaves the column's rows.
local function planTrees(job)
    local random = columnRandom(job.cx, job.cz)
    local planned = {}
    for gz = 0, 3 do
        for gx = 0, 3 do
            local lx = 3 + gx * TREE_GRID + random:NextInteger(0, TREE_GRID - 1)
            local lz = 3 + gz * TREE_GRID + random:NextInteger(0, TREE_GRID - 1)
            local roll, shape = random:NextNumber(), random:NextNumber()
            local x, z = job.x0 + lx - 1, job.z0 + lz - 1
            local ground = job.heights[(lz - 1) * CHUNK + lx]
            -- WHY no wood in the spawn's column: it is made while the player waits, so it stays light; the woods
            -- around it are in the first view.
            local chance = not job.isSpawnColumn and forest(x, z) > WOOD and WOOD_CHANCE or FIELD_CHANCE
            local kind = shape < 0.6 and "oak" or shape < 0.85 and "tall" or "big"
            local trunk = kind == "tall" and 6 + (roll < chance / 2 and 1 or 0) or kind == "big" and 6
                or 4 + (roll < chance / 2 and 1 or 0)
            local clear = roll < chance and ground > SHORE and ground < ROCK_FROM
                and ground + trunk + 2 <= TOP - 1 and not job.mouth[(lz - 1) * CHUNK + lx]
                and drop(job.heights, lx, lz, ground) < STEEP
                and (math.abs(x - spawnCellX) > 2 or math.abs(z - spawnCellZ) > 2)
            for _, other in ipairs(planned) do
                if math.abs(other.lx - lx) < 2 and math.abs(other.lz - lz) < 2 then
                    clear = false
                end
            end
            if clear then
                planned[#planned + 1] = { lx = lx, lz = lz, ground = ground, trunk = trunk, kind = kind }
            end
        end
    end
    return planned
end

-- Runs one phase (or one budgeted slice of it). Returns true when the column is complete.
local function stepJob(job, heightBudget, pillarBudget)
    if job.phase == "heights" then
        local last = math.min(job.next + heightBudget - 1, PILLARS)
        for index = job.next, last do
            local h = surfaceHeight(job.x0 + (index - 1) % CHUNK, job.z0 + math.floor((index - 1) / CHUNK))
            job.heights[index] = h
            if h < job.minHeight then
                job.minHeight = h
            end
        end
        job.next = last + 1
        if job.next > PILLARS then
            job.phase = "base"
        end
    elseif job.phase == "base" then
        -- WHY 13 rows under the lowest surface: the deepest cave row is 9 + 3 under its pillar's surface. The spawn's
        -- column has no caves, so its band stays shallow and it is ready soonest.
        job.isSpawnColumn = job.cx == spawnColumnKeyX and job.cz == spawnColumnKeyZ
        job.caves = not job.isSpawnColumn
        local bandBottom = math.max(BOTTOM + 1, job.minHeight - (job.caves and 13 or 3))
        if bandBottom > BOTTOM + 1 then
            terrain:ReplaceMaterial(columnRegion(job.cx, job.cz, BOTTOM + 1, bandBottom - 1), 4, AIR, STONE)
        end
        job.bandBottom = bandBottom
        job.band = columnRegion(job.cx, job.cz, bandBottom, TOP - 1)
        job.materials, job.occupancy = terrain:ReadVoxels(job.band, 4)
        planMouth(job)
        job.phase = "pillars"
        job.next = 1
    elseif job.phase == "pillars" then
        local work = 0
        while job.next <= PILLARS do
            local cost = pillarWork(job, job.next)
            if work > 0 and work + cost > pillarBudget then
                break
            end
            fillPillar(job, job.next)
            work = work + cost
            job.next = job.next + 1
        end
        if job.next > PILLARS then
            job.phase = "trees"
            job.next = 1
        end
    elseif job.phase == "trees" then
        if not job.trees then
            job.trees = planTrees(job)
            job.treeRandom = columnRandom(job.cx, job.cz + 4096)
            return false
        end
        local spent = 0
        while true do
            local tree = job.trees[job.next]
            if not tree then
                job.phase = "write"
                break
            end
            local cost = TREE_COST[tree.kind]
            if spent > 0 and spent + cost > TREE_FRAME_COST then
                break
            end
            growTree(job, tree, job.treeRandom)
            spent = spent + cost
            job.next = job.next + 1
        end
    else
        terrain:WriteVoxels(job.band, 4, job.materials, job.occupancy)
        -- WHY last: the bedrock row marks the column as done, so an interrupted column is redone.
        terrain:ReplaceMaterial(columnRegion(job.cx, job.cz, BOTTOM, BOTTOM), 4, AIR, BEDROCK)
        return true
    end
    return false
end

-- ---- streaming around the player -----------------------------------------------------------------

local generated = {}
local columnsDone = 0
local columnsKnown = 0          -- made this run or found made: what the first view waits for
local job = nil
local stopped = false
workspace:SetAttribute("BlockWorldColumns", 0)
workspace:SetAttribute("BlockWorldReady", false)
workspace:SetAttribute("BlockWorldOpened", false)
workspace:SetAttribute("BlockWorldOpeningColumns", OPENING_COLUMNS)

-- WHY the nearest column left out, measured from the worst place in the player's column: the view radius is a
-- circle of columns, so the land reaches less far along a diagonal and from a column's corner than straight ahead
-- (4 columns: 181 studs, not 256). Past this distance a player may see where the world ends.
local function landReach()
    local nearest = math.huge
    for dx = -viewRadius - 1, viewRadius + 1 do
        for dz = -viewRadius - 1, viewRadius + 1 do
            if dx * dx + dz * dz > viewRadius * viewRadius + 1 then
                local gx = math.max(math.abs(dx) - 1, 0)
                local gz = math.max(math.abs(dz) - 1, 0)
                nearest = math.min(nearest, math.sqrt(gx * gx + gz * gz))
            end
        end
    end
    return nearest * CHUNK * CELL
end
workspace:SetAttribute("BlockWorldViewStuds", landReach())

local function finishColumn(finished)
    generated[columnKey(finished.cx, finished.cz)] = true
    columnsDone = columnsDone + 1
    columnsKnown = columnsKnown + 1
    workspace:SetAttribute("BlockWorldColumns", columnsDone)
end

local function localCharacter()
    local player = Players.LocalPlayer or Players:GetPlayers()[1]
    return player and player.Character
end

local function rootPart(character)
    return character and character:FindFirstChild("HumanoidRootPart")
end

local function streamCenter()
    local root = rootPart(localCharacter())
    if root then
        return root.Position
    end
    local camera = workspace.CurrentCamera
    return camera and camera.CFrame.Position or Vector3.new(0, 0, 0)
end

-- The next column inside the view radius this run has not generated: the one the player stands in, then the
-- one whose nearest edge is nearest the player (a player by a column's edge sees the next column at once), where a
-- column in front of the camera counts as up to LOOK_PULL columns nearer, so the view fills in before what is
-- behind the player (ties: lowest dx, then dz).
local sqrt = math.sqrt
local function nextMissing(center)
    local fx = center.X / (CHUNK * CELL)
    local fz = center.Z / (CHUNK * CELL)
    local ccx, ccz = math.floor(fx), math.floor(fz)
    fx, fz = fx - ccx, fz - ccz
    if not generated[columnKey(ccx, ccz)] then
        return ccx, ccz
    end
    local lookX, lookZ = 0, 0
    local camera = workspace.CurrentCamera
    if camera then
        local look = camera.CFrame.LookVector
        local flat = math.sqrt(look.X * look.X + look.Z * look.Z)
        if flat > 0.1 then
            lookX, lookZ = LOOK_PULL * look.X / flat, LOOK_PULL * look.Z / flat
        end
    end
    local bestX, bestZ, bestScore = nil, nil, math.huge
    local limit = viewRadius * viewRadius + 1
    for dx = -viewRadius, viewRadius do
        for dz = -viewRadius, viewRadius do
            local distance = dx * dx + dz * dz
            if distance <= limit then
                local ex = dx - fx
                if ex < 0 then
                    ex = math.max(fx - dx - 1, 0)
                end
                local ez = dz - fz
                if ez < 0 then
                    ez = math.max(fz - dz - 1, 0)
                end
                local tx, tz = dx + 0.5 - fx, dz + 0.5 - fz
                local score = ex + ez - (tx * lookX + tz * lookZ) / sqrt(tx * tx + tz * tz)
                if score < bestScore and not generated[columnKey(ccx + dx, ccz + dz)] then
                    bestX, bestZ, bestScore = ccx + dx, ccz + dz, score
                end
            end
        end
    end
    return bestX, bestZ
end

local opened = false
local heldRoot = nil

-- The first view is built (or the wait is over): the held character is let go.
local function becomeOpened()
    if opened then
        return
    end
    opened = true
    workspace:SetAttribute("BlockWorldOpened", true)
    if heldRoot then
        pcall(function()
            heldRoot.Anchored = false
        end)
        heldRoot = nil
    end
end

local function stopStreaming(reason)
    becomeOpened()
    stopped = true
    job = nil
    workspace:SetAttribute("BlockWorldNotice", "The world stopped growing: " .. reason)
    print("[blockworld] the world stopped growing: " .. reason)
end

-- ---- the spawn -------------------------------------------------------------------------------------

-- How good the first view from pillar (x0, z0) at height h0 along heading `angle` is, from the heights alone: a rise
-- in the middle distance (a hill or a peak, snow counts extra), water, a beach and a wood near enough to see, and a
-- penalty when a slope right in front blocks the view.
local spawnHeights = {}       -- the spawn search's heights, read once each (the search runs while the mod loads)
local function spawnSearchHeight(x, z)
    local key = x * 65536 + z
    local h = spawnHeights[key]
    if not h then
        h = surfaceHeight(x, z)
        spawnHeights[key] = h
    end
    return h
end

local function viewScore(x0, z0, h0, angle)
    local dx, dz = math.cos(angle), math.sin(angle)
    local rise, snow, sea, shore, wood, blocked = 0, false, false, false, false, false
    for step = 6, 42, 6 do
        local x, z = x0 + math.round(dx * step), z0 + math.round(dz * step)
        local h = spawnSearchHeight(x, z)
        if step <= 10 and h - h0 > 3 then
            blocked = true
        end
        if step >= 12 then
            rise = math.max(rise, h - h0)
            snow = snow or h >= SNOW_FROM
        end
        sea = sea or h <= WATER
        shore = shore or (h > WATER and h <= SHORE)
        if step <= 26 and h > SHORE and h < ROCK_FROM and forest(x, z) > WOOD then
            wood = true
        end
    end
    return math.min(rise, 10) + (snow and 3 or 0) + (sea and 5 or 0) + (shore and 2 or 0) + (wood and 3 or 0)
        - (blocked and 8 or 0)
end

-- The spawn and its heading: of nine lowland pillars around the middle of the first column (rows 1 to 4) and eight
-- headings each, the pair with the best view (a little closer to the middle wins a tie). From the heights alone, so a
-- saved world finds the same spawn. WHY eight headings and seven samples a ray: the search runs while the mod loads,
-- which must fit an AI tool call (100 ms); about 450 heights do.
local function chooseSpawn()
    local best = nil
    for dz = -4, 4, 4 do
        for dx = -4, 4, 4 do
            local x0, z0 = SPAWN_FROM + dx, SPAWN_FROM + dz
            local h0 = spawnSearchHeight(x0, z0)
            if h0 >= 1 and h0 <= 4 then
                for index = 0, 7 do
                    local angle = index * math.pi / 4
                    local score = viewScore(x0, z0, h0, angle) - 0.15 * (math.abs(dx) + math.abs(dz))
                    if not best or score > best.score then
                        best = { score = score, x = x0, z = z0, h = h0, angle = angle }
                    end
                end
            end
        end
    end
    return best
end

-- The land pillar nearest the middle of the column at the origin (SPAWN_FROM), ring by ring: the spawn when no
-- lowland lies around that middle. A seed with no land that close spawns at the middle.
local function findSpawn()
    for ring = 0, SPAWN_SEARCH do
        local bestX, bestZ, bestHeight, bestDistance = nil, nil, nil, math.huge
        for x = -ring, ring do
            local step = (x == -ring or x == ring) and 1 or 2 * ring
            for z = -ring, ring, math.max(step, 1) do
                local h = surfaceHeight(SPAWN_FROM + x, SPAWN_FROM + z)
                local distance = x * x + z * z
                if h > SHORE and distance < bestDistance then
                    bestX, bestZ, bestHeight, bestDistance = x, z, h, distance
                end
            end
        end
        if bestX then
            return SPAWN_FROM + bestX, SPAWN_FROM + bestZ, bestHeight
        end
    end
    return SPAWN_FROM, SPAWN_FROM, math.max(surfaceHeight(SPAWN_FROM, SPAWN_FROM), WATER)
end

local chosen = chooseSpawn()
local spawnX, spawnZ, spawnHeight
if chosen then
    spawnX, spawnZ, spawnHeight = chosen.x, chosen.z, chosen.h
else
    spawnX, spawnZ, spawnHeight = findSpawn()
end
spawnCellX, spawnCellZ = spawnX, spawnZ
spawnColumnKeyX, spawnColumnKeyZ = math.floor(spawnX / CHUNK), math.floor(spawnZ / CHUNK)
local spawn = Vector3.new(spawnX * CELL + CELL / 2, (spawnHeight + 1) * CELL + 4, spawnZ * CELL + CELL / 2)
local spawnColumnX, spawnColumnZ = math.floor(spawnX / CHUNK), math.floor(spawnZ / CHUNK)
workspace:SetAttribute("BlockWorldSpawn", spawn)

-- The heading a new player first looks along: the chosen spawn's best view, or (for the fallback spawn) the best of
-- sixteen from there.
local bestHeading = chosen and chosen.angle or 0
if not chosen then
    local bestScore = -math.huge
    for index = 0, 15 do
        local heading = index * math.pi / 8
        local score = viewScore(spawnX, spawnZ, spawnHeight, heading)
        if score > bestScore then
            bestHeading, bestScore = heading, score
        end
    end
end
local spawnLook = Vector3.new(math.cos(bestHeading), 0, math.sin(bestHeading))
workspace:SetAttribute("BlockWorldSpawnLook", spawnLook)

local ready = false

-- Until the first view exists the character waits at the spawn, anchored, instead of falling into the void.
local function moveToSpawn(character)
    if not character then
        return
    end
    character:PivotTo(CFrame.lookAt(spawn, spawn + spawnLook))
    local root = rootPart(character)
    if root and not opened then
        pcall(function()
            root.Anchored = true
        end)
        heldRoot = root
    end
end

local function becomeReady()
    if ready then
        return
    end
    ready = true
    workspace:SetAttribute("BlockWorldReady", true)
    print("[blockworld] world seed " .. seed .. " - the spawn is ready, the rest streams in around you.")
end

local function insideBlock(position)
    local cell = terrain:WorldToCell(position)
    local corner = terrain:CellCornerToWorld(cell.X, cell.Y, cell.Z)
    local materials, occupancy = terrain:ReadVoxels(Region3.new(corner, corner + Vector3.new(CELL, CELL, CELL)), 4)
    return occupancy[1][1][1] > 0 and materials[1][1][1] ~= SEA
end

-- A fresh world holds the character at the spawn; a saved one only rescues a character stuck in a block.
local function placeCharacter(character)
    local root = rootPart(character)
    if not root then
        return
    end
    if not isGenerated(spawnColumnX, spawnColumnZ) or root.Position.Y < FALL_FLOOR or insideBlock(root.Position) then
        moveToSpawn(character)
    end
end

local function watchPlayer(player)
    player.CharacterAdded:Connect(moveToSpawn)
    if player.Character then
        placeCharacter(player.Character)
    end
end

for _, player in ipairs(Players:GetPlayers()) do
    watchPlayer(player)
end
Players.PlayerAdded:Connect(watchPlayer)

-- The spawn column comes first, wherever the camera is; a saved world already has it, and its first view.
if isGenerated(spawnColumnX, spawnColumnZ) then
    generated[columnKey(spawnColumnX, spawnColumnZ)] = true
    becomeReady()
    becomeOpened()
else
    job = newJob(spawnColumnX, spawnColumnZ)
end

-- ---- streaming loop -------------------------------------------------------------------------------

local fallCheck = 0
local openingWait = 0
RunService.Heartbeat:Connect(function(dt)
    if ready and not opened then
        openingWait = openingWait + dt
        if columnsKnown >= OPENING_COLUMNS or openingWait >= OPENING_SECONDS then
            becomeOpened()
        end
    end

    fallCheck = fallCheck + dt
    if fallCheck >= 0.5 then
        fallCheck = 0
        local character = localCharacter()
        local root = rootPart(character)
        if root and root.Position.Y < FALL_FLOOR then
            moveToSpawn(character)
        end
    end

    if stopped then
        return
    end

    -- WHY a frame of its own: choosing the next column scans the whole view radius, so its first step waits for
    -- the next frame and the two never share one frame's budget.
    if job == nil then
        local center = streamCenter()
        for _ = 1, PROBE_BUDGET do
            local cx, cz = nextMissing(center)
            if cx == nil then
                becomeOpened()
                return
            end
            if isGenerated(cx, cz) then
                generated[columnKey(cx, cz)] = true
                columnsKnown = columnsKnown + 1
            else
                job = newJob(cx, cz)
                return
            end
        end
        return
    end

    local ok, finishedOrError = pcall(stepJob, job, HEIGHT_BUDGET, PILLAR_BUDGET)
    if not ok then
        stopStreaming(tostring(finishedOrError))
    elseif finishedOrError then
        finishColumn(job)
        job = nil
        becomeReady()
    end
end)
