--[[@coreai
id: blockworld_world
name: Block World 1 - World
version: 1.0.0
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo (the seven mods of the Samples/Block World folder; turn the whole folder on). Generates a seeded voxel world in workspace.Terrain - rolling hills, a sea level, sand at the shore, dirt and stone below, an unbreakable bedrock floor and trees - and streams it around the player in budgeted steps, a few dozen pillars per frame: the column at the spawn comes first (the character waits there, anchored, for the third of a second it takes), then the rest, nearest first. The same seed always gives the same world; set the workspace attribute BlockWorldSeed before the mod loads to pick another. Generation only fills empty cells, so a saved build is never overwritten.
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
-- Other Block World mods read what this one publishes as workspace attributes:
--   BlockWorldSeed (number), BlockWorldReady (true once the spawn column exists),
--   BlockWorldSpawn (Vector3, studs), BlockWorldColumns (columns generated), BlockWorldNotice (text).

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
local PILLAR_BUDGET = 24        -- pillars one frame may fill
local PROBE_BUDGET = 6          -- "is this column already generated?" reads per frame
local SPAWN_SEARCH = 24         -- rings of pillars searched for land around the origin
local MAX_TREES = 3             -- per column
local FALL_FLOOR = BOTTOM * CELL - 24

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

-- ---- one column, phase by phase ------------------------------------------------------------------

local function newJob(cx, cz)
    return {
        cx = cx, cz = cz, x0 = cx * CHUNK, z0 = cz * CHUNK,
        phase = "heights", next = 1, heights = {}, minHeight = TOP,
    }
end

-- Fills rows y0..y1 of one pillar with `material`, leaving every cell that is already occupied.
local function fillRun(materialColumn, occupancyColumn, lz, y0, y1, material, rowBase)
    for y = y0, y1 do
        local row = y - rowBase
        local occupancyRow = occupancyColumn[row]
        if occupancyRow[lz] == 0 then
            occupancyRow[lz] = 1
            materialColumn[row][lz] = material
        end
    end
end

local function fillPillar(job, index)
    local lx = (index - 1) % CHUNK + 1
    local lz = math.floor((index - 1) / CHUNK) + 1
    local h = job.heights[index]
    local materialColumn, occupancyColumn = job.materials[lx], job.occupancy[lx]
    local base = job.bandBottom
    local rowBase = base - 1
    local sandy = h <= SHORE
    fillRun(materialColumn, occupancyColumn, lz, base, h - 4, STONE, rowBase)
    fillRun(materialColumn, occupancyColumn, lz, math.max(base, h - 3), h - 1, sandy and SAND or DIRT, rowBase)
    fillRun(materialColumn, occupancyColumn, lz, h, h, sandy and SAND or GRASS, rowBase)
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
        local clear = ground > SHORE
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
        local bandBottom = math.max(BOTTOM + 1, job.minHeight - 3)
        if bandBottom > BOTTOM + 1 then
            terrain:ReplaceMaterial(columnRegion(job.cx, job.cz, BOTTOM + 1, bandBottom - 1), 4, AIR, STONE)
        end
        job.bandBottom = bandBottom
        job.band = columnRegion(job.cx, job.cz, bandBottom, TOP - 1)
        job.materials, job.occupancy = terrain:ReadVoxels(job.band, 4)
        job.phase = "pillars"
        job.next = 1
    elseif job.phase == "pillars" then
        local last = math.min(job.next + pillarBudget - 1, PILLARS)
        for index = job.next, last do
            fillPillar(job, index)
        end
        job.next = last + 1
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
local job = nil
local stopped = false
workspace:SetAttribute("BlockWorldColumns", 0)
workspace:SetAttribute("BlockWorldReady", false)

local function finishColumn(finished)
    generated[columnKey(finished.cx, finished.cz)] = true
    columnsDone = columnsDone + 1
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

-- The nearest column inside the view radius this run has not generated (ties: lowest dx, then dz).
local function nearestMissing(center)
    local ccx = math.floor(center.X / (CHUNK * CELL))
    local ccz = math.floor(center.Z / (CHUNK * CELL))
    local bestX, bestZ, bestDistance = nil, nil, math.huge
    local limit = viewRadius * viewRadius + 1
    for dx = -viewRadius, viewRadius do
        for dz = -viewRadius, viewRadius do
            local distance = dx * dx + dz * dz
            if distance <= limit and distance < bestDistance and not generated[columnKey(ccx + dx, ccz + dz)] then
                bestX, bestZ, bestDistance = ccx + dx, ccz + dz, distance
            end
        end
    end
    return bestX, bestZ
end

local function stopStreaming(reason)
    stopped = true
    job = nil
    workspace:SetAttribute("BlockWorldNotice", "The world stopped growing: " .. reason)
    print("[blockworld] the world stopped growing: " .. reason)
end

-- ---- the spawn -------------------------------------------------------------------------------------

-- The land pillar nearest the origin, ring by ring, from the heights alone (a saved world finds the same
-- spawn). The first ring that holds land ends the search; a seed with no land that close spawns at the origin.
local function findSpawn()
    for ring = 0, SPAWN_SEARCH do
        local bestX, bestZ, bestHeight, bestDistance = nil, nil, nil, math.huge
        for x = -ring, ring do
            local step = (x == -ring or x == ring) and 1 or 2 * ring
            for z = -ring, ring, math.max(step, 1) do
                local h = surfaceHeight(x, z)
                local distance = x * x + z * z
                if h > SHORE and distance < bestDistance then
                    bestX, bestZ, bestHeight, bestDistance = x, z, h, distance
                end
            end
        end
        if bestX then
            return bestX, bestZ, bestHeight
        end
    end
    return 0, 0, math.max(surfaceHeight(0, 0), WATER)
end

local spawnX, spawnZ, spawnHeight = findSpawn()
local spawn = Vector3.new(spawnX * CELL + CELL / 2, (spawnHeight + 1) * CELL + 4, spawnZ * CELL + CELL / 2)
local spawnColumnX, spawnColumnZ = math.floor(spawnX / CHUNK), math.floor(spawnZ / CHUNK)
workspace:SetAttribute("BlockWorldSpawn", spawn)

local ready = false
local heldRoot = nil

-- Until the spawn column exists the character waits there, anchored, instead of falling into the void.
local function moveToSpawn(character)
    if not character then
        return
    end
    character:PivotTo(CFrame.new(spawn))
    local root = rootPart(character)
    if root and not ready then
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
    if heldRoot then
        pcall(function()
            heldRoot.Anchored = false
        end)
        heldRoot = nil
    end
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

-- The spawn column comes first, wherever the camera is; a saved world already has it.
if isGenerated(spawnColumnX, spawnColumnZ) then
    generated[columnKey(spawnColumnX, spawnColumnZ)] = true
    becomeReady()
else
    job = newJob(spawnColumnX, spawnColumnZ)
end

-- ---- streaming loop -------------------------------------------------------------------------------

local fallCheck = 0
RunService.Heartbeat:Connect(function(dt)
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

    if job == nil then
        local center = streamCenter()
        for _ = 1, PROBE_BUDGET do
            local cx, cz = nearestMissing(center)
            if cx == nil then
                return
            end
            if isGenerated(cx, cz) then
                generated[columnKey(cx, cz)] = true
            else
                job = newJob(cx, cz)
                break
            end
        end
        if job == nil then
            return
        end
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
