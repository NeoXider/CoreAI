--[[@coreai
id: blockworld_world
name: Block World 1 - World
version: 1.2.0
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo (the eight mods of the Samples/Block World folder; turn the whole folder on). Generates a seeded voxel world in workspace.Terrain - rolling hills, a sea level, sand at the shore, dirt and stone below, winding caves with a few larger chambers and entrances here and there (flooded below the sea level at the shore), an unbreakable bedrock floor and trees - and streams it around the player in budgeted steps, a few dozen pillars per frame: the column at the spawn comes first (the character waits there, anchored, for the third of a second it takes), then the rest, nearest first. The same seed always gives the same world; set the workspace attribute BlockWorldSeed before the mod loads to pick another. Generation only fills empty cells, so a saved build is never overwritten.
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
local WATER = -2                -- top row of the sea
local SHORE = WATER + 1         -- pillars this low get sand instead of grass
local VIEW_RADIUS = 4           -- columns streamed around the player
local HEIGHT_BUDGET = 48        -- surface heights one frame may sample
-- WHY 250: a cell costs about 15 instructions, so the fullest frame stays near 4,000 of the 5,000 (half the default
-- resume budget) the tests hold every streaming step to; 350 already went over.
local PILLAR_BUDGET = 250      -- work one frame may spend filling pillars, in cells (see pillarWork)
local PILLAR_WORK = 8           -- a pillar's own cost, in cells
local CAVE_WORK = 24            -- the most a pillar's caves cost, in cells: up to thirteen rows of 3D noise
local OPENING_COLUMNS = 4       -- columns made before a new player is let go: the spawn's and the three it faces
local OPENING_SECONDS = 6       -- the longest a new player waits for them
local PROBE_BUDGET = 1          -- "is this column already generated?" reads per frame (each scans the radius)
local LOOK_PULL = 0.5           -- columns a column straight ahead of the camera counts as nearer
local SPAWN_SEARCH = 24         -- rings of pillars searched for land around the middle of the first column
-- WHY the middle: a player standing there sees a whole column of ground around them at once, while the columns
-- beyond it are still being made.
local SPAWN_FROM = CHUNK / 2
local MAX_TREES = 3             -- per column
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
local SEA = Enum.Material.Water
local TRUNK = Enum.Material.WoodPlanks
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

-- The top solid row of the pillar at cell (x, z).
local function surfaceHeight(x, z)
    local continent = noise((x + OFFSET_X) / 90, (z + OFFSET_Z) / 90, 0.25)
    local hills = noise((x + OFFSET_Z) / 26, (z + OFFSET_X) / 26, 0.75)
    local bumps = noise((x - OFFSET_X) / 7, (z - OFFSET_Z) / 7, 0.5)
    local height = -3 + continent * 24 + hills * 9 * math.max(0, continent + 0.3) + bumps * 1.5
    return math.clamp(math.round(height), BOTTOM + 4, TOP - 8)
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
-- the tunnel widen into a chamber. Where the entrance noise is high the band reaches the surface, so a player
-- finds a way in now and then. The rock stays closed under the sea and the shore, the row above bedrock stays,
-- and a cave at the shore or under the sea is flooded up to the sea level; inland caves stay dry.

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
    elseif noise((x + OFFSET_X) / 29, (z + OFFSET_Z) / 29, 6.5) > 0.42 then
        top = h
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
        phase = "heights", next = 1, heights = {}, minHeight = TOP,
    }
end

-- Fills rows y0..y1 of one pillar with `material`, leaving every cell that is already occupied; a cave row
-- stays empty, or holds water at or below the sea level where the pillar is at the shore or under the sea.
local function fillRun(materialColumn, occupancyColumn, lz, y0, y1, material, rowBase, cave, flooded)
    for y = y0, y1 do
        local row = y - rowBase
        local occupancyRow = occupancyColumn[row]
        if occupancyRow[lz] == 0 then
            if not (cave and cave[y]) then
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

local function fillPillar(job, index)
    local lx = (index - 1) % CHUNK + 1
    local lz = math.floor((index - 1) / CHUNK) + 1
    local h = job.heights[index]
    local materialColumn, occupancyColumn = job.materials[lx], job.occupancy[lx]
    local base = job.bandBottom
    local rowBase = base - 1
    local sandy = h <= SHORE
    local cave = job.caves and caveRows(job.x0 + lx - 1, job.z0 + lz - 1, h) or nil
    local flooded = h <= SHORE + 2
    fillRun(materialColumn, occupancyColumn, lz, base, h - 4, STONE, rowBase, cave, flooded)
    fillRun(materialColumn, occupancyColumn, lz, math.max(base, h - 3), h - 1, sandy and SAND or DIRT, rowBase, cave,
        flooded)
    fillRun(materialColumn, occupancyColumn, lz, h, h, sandy and SAND or GRASS, rowBase, cave, flooded)
    fillRun(materialColumn, occupancyColumn, lz, h + 1, WATER, SEA, rowBase)
end

local function setCell(job, lx, y, lz, material)
    local row = y - job.bandBottom + 1
    local occupancyRow = job.occupancy[lx][row]
    if occupancyRow[lz] == 0 then
        occupancyRow[lz] = 1
        job.materials[lx][row][lz] = material
    end
end

local function growTree(job, lx, lz, ground, trunkHeight)
    local top = ground + trunkHeight
    for y = ground + 1, top do
        setCell(job, lx, y, lz, TRUNK)
    end
    for dy = -1, 1 do
        local radius = dy == 1 and 1 or 2
        for dx = -radius, radius do
            for dz = -radius, radius do
                if radius < 2 or math.abs(dx) < 2 or math.abs(dz) < 2 then
                    setCell(job, lx + dx, top + dy, lz + dz, LEAVES)
                end
            end
        end
    end
    setCell(job, lx, top + 2, lz, LEAVES)
end

-- Chooses this column's trees (where, how tall) without writing anything; one is grown per frame.
local function planTrees(job)
    local random = columnRandom(job.cx, job.cz)
    local planned = {}
    for _ = 1, random:NextInteger(0, MAX_TREES) do
        local lx = random:NextInteger(3, CHUNK - 2)
        local lz = random:NextInteger(3, CHUNK - 2)
        local trunkHeight = random:NextInteger(4, 5)
        local ground = job.heights[(lz - 1) * CHUNK + lx]
        local entrance = caveRows(job.x0 + lx - 1, job.z0 + lz - 1, ground)
        local clear = ground > SHORE and not (entrance and entrance[ground])
        for _, other in ipairs(planned) do
            if math.abs(other.lx - lx) < 4 and math.abs(other.lz - lz) < 4 then
                clear = false
            end
        end
        if clear then
            planned[#planned + 1] = { lx = lx, lz = lz, ground = ground, trunk = trunkHeight }
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
        job.caves = not (job.cx == spawnColumnKeyX and job.cz == spawnColumnKeyZ)
        local bandBottom = math.max(BOTTOM + 1, job.minHeight - (job.caves and 13 or 3))
        if bandBottom > BOTTOM + 1 then
            terrain:ReplaceMaterial(columnRegion(job.cx, job.cz, BOTTOM + 1, bandBottom - 1), 4, AIR, STONE)
        end
        job.bandBottom = bandBottom
        job.band = columnRegion(job.cx, job.cz, bandBottom, TOP - 1)
        job.materials, job.occupancy = terrain:ReadVoxels(job.band, 4)
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
        job.trees = job.trees or planTrees(job)
        local tree = job.trees[job.next]
        if tree then
            growTree(job, tree.lx, tree.lz, tree.ground, tree.trunk)
            job.next = job.next + 1
        else
            job.phase = "write"
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

-- The land pillar nearest the middle of the column at the origin (SPAWN_FROM), ring by ring, from the heights alone
-- (a saved world finds the same spawn). The first ring that holds land ends the search; a seed with no land that
-- close spawns at that middle.
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

local spawnX, spawnZ, spawnHeight = findSpawn()
spawnCellX, spawnCellZ = spawnX, spawnZ
spawnColumnKeyX, spawnColumnKeyZ = math.floor(spawnX / CHUNK), math.floor(spawnZ / CHUNK)
local spawn = Vector3.new(spawnX * CELL + CELL / 2, (spawnHeight + 1) * CELL + 4, spawnZ * CELL + CELL / 2)
local spawnColumnX, spawnColumnZ = math.floor(spawnX / CHUNK), math.floor(spawnZ / CHUNK)
workspace:SetAttribute("BlockWorldSpawn", spawn)

-- The heading a new player first looks along: of sixteen, the one whose next 30 pillars show the most - a hill that
-- rises above the spawn, the sea, its sandy shore. From the heights alone, so the same seed faces the same way.
local function viewScore(dx, dz)
    local rise, sea, shore = 0, false, false
    for step = 6, 30, 2 do
        local h = surfaceHeight(spawnX + math.round(dx * step), spawnZ + math.round(dz * step))
        rise = math.max(rise, h - spawnHeight)
        if h <= WATER then
            sea = true
        elseif h <= SHORE then
            shore = true
        end
    end
    return rise + (sea and 6 or 0) + (shore and 3 or 0)
end

local bestHeading, bestScore = 0, -math.huge
for index = 0, 15 do
    local heading = index * math.pi / 8
    local score = viewScore(math.cos(heading), math.sin(heading))
    if score > bestScore then
        bestHeading, bestScore = heading, score
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
