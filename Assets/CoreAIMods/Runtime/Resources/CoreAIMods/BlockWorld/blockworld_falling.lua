--[[@coreai
id: blockworld_falling
name: Block World 9 - Falling blocks
version: 1.0.2
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo. Sand with nothing under it falls, as in a block game: when the block under it is broken (by the player, the autopilot or the AI) or when it is placed in the air, the whole run of sand above falls one block every tenth of a second until it rests on something. It falls through water too, and the water closes over the cell it leaves. Which materials fall is one small table (FALLS), so another material can join. The land round the player is watched; land farther off settles when the player comes near, and a world saved in the middle of a fall carries on falling when it is opened again.
]]

-- How it works. Each frame one slab of the box round the player (eleven blocks each way, from eight rows under the
-- feet to ten over them) is read with ReadVoxels; the lowest falling-material cell of a pillar with air or water
-- under it starts a fall of the whole run above it. A falling run moves one row down every STEP_SECONDS of game
-- time (frame-rate independent), writing only the two cells that change, until the cell under it is solid. Every
-- write stays inside a generated column (one with its bedrock row), and the pillars with a fall in progress are kept
-- in the workspace attribute BlockWorldFalling ("x,z;x,z"), so a reload or a saved world picks them up again.
--
-- Reads: the Terrain, the local character (where to watch). Writes: Terrain cells, BlockWorldFalling.

local Players = game:GetService("Players")
local RunService = game:GetService("RunService")

local terrain = workspace.Terrain

local CELL = 4
local BEDROCK_ROW = -16
local TOP_ROW = 15
local STEP_SECONDS = 0.1        -- a falling run moves one block this often
local WATCH = 5                 -- blocks each way round the player whose sand is watched
local WATCH_BELOW = 8           -- rows under the player's feet that are watched
local WATCH_ABOVE = 10          -- rows over them
local MAX_RUNS = 48             -- runs falling at once; more wait for the next look
local MOVES_PER_FRAME = 12      -- moves one frame may make (each writes at most two cells for a run of one material)

-- The rule table: a material listed here falls when the cell under it is air or water.
local FALLS = { [Enum.Material.Sand] = true }

local AIR = Enum.Material.Air
local WATER = Enum.Material.Water
local BEDROCK = Enum.Material.Basalt

local runs = {}                 -- "x,z" -> { x, z, bottom, top, materials (bottom first), wait }
local runCount = 0
local sweepRow = 0
local dirty = false

local function open(material)
    return material == AIR or material == WATER
end

local function readBox(x0, y0, z0, x1, y1, z1)
    local min = terrain:CellCornerToWorld(x0, y0, z0)
    local max = terrain:CellCornerToWorld(x1 + 1, y1 + 1, z1 + 1)
    return terrain:ReadVoxels(Region3.new(min, max), 4)
end

local function cellMaterial(x, y, z)
    return readBox(x, y, z, x, y, z)[1][1][1]
end

local function columnMade(x, z)
    return cellMaterial(x, BEDROCK_ROW, z) == BEDROCK
end

local function writeCell(x, y, z, material)
    terrain:FillBlock(CFrame.new(terrain:CellCenterToWorld(x, y, z)), Vector3.new(CELL, CELL, CELL), material)
end

local function localCharacter()
    local player = Players.LocalPlayer or Players:GetPlayers()[1]
    return player and player.Character
end

-- The lowest and highest points of a character's own parts: its feet and the top of its head, whatever its size.
local function bodySpan(character)
    local low, high = math.huge, -math.huge
    for _, part in ipairs(character:GetChildren()) do
        if part:IsA("BasePart") then
            local half = part.Size.Y / 2
            low = math.min(low, part.Position.Y - half)
            high = math.max(high, part.Position.Y + half)
        end
    end
    return low, high
end

-- The cells the local character fills (its pillar, from the feet to the head), so sand never falls into it.
local function standsIn(x, y, z)
    local character = localCharacter()
    local root = character and character:FindFirstChild("HumanoidRootPart")
    if not root then
        return false
    end
    local p = root.Position
    local low, high = bodySpan(character)
    local feet = math.floor((low + 0.01) / CELL)
    local head = math.floor((high - 0.01) / CELL)
    return x == math.floor(p.X / CELL) and z == math.floor(p.Z / CELL) and y >= feet and y <= head
end

-- Starts the fall of the run whose lowest cell is (x, y, z), when that cell falls, the cell under it is open and the
-- column is generated. A pillar falls one run at a time; a run higher up starts once the lower one has landed.
local function start(x, y, z)
    local key = x .. "," .. z
    if runs[key] or runCount >= MAX_RUNS or y - 1 <= BEDROCK_ROW or not columnMade(x, z) then
        return
    end
    local pillar = readBox(x, y - 1, z, x, TOP_ROW, z)[1]
    if not FALLS[pillar[2][1]] or not open(pillar[1][1]) then
        return
    end
    local materials = {}
    local index = 2
    while index <= #pillar and FALLS[pillar[index][1]] do
        materials[#materials + 1] = pillar[index][1]
        index = index + 1
    end
    runs[key] = { x = x, z = z, bottom = y, top = y + #materials - 1, materials = materials, wait = 0 }
    runCount = runCount + 1
    dirty = true
end

-- WHY water closes over the cell a run leaves when water lies beside it: a pool keeps its surface, and sand that
-- sank through it leaves no hole of air under the water.
local function leftBehind(x, y, z)
    local ring = readBox(x - 1, y, z - 1, x + 1, y, z + 1)
    if ring[1][1][2] == WATER or ring[3][1][2] == WATER or ring[2][1][1] == WATER or ring[2][1][3] == WATER then
        return WATER
    end
    return AIR
end

-- Moves the run one row down; false when it rests (the cell under it is solid, the bedrock, or the player).
local function move(run)
    local below = run.bottom - 1
    if below <= BEDROCK_ROW then
        return false
    end
    local under = cellMaterial(run.x, below, run.z)
    if not open(under) then
        return false
    end
    if standsIn(run.x, below, run.z) then
        return true
    end
    local count = #run.materials
    for index = 1, count do
        local row = below + index - 1
        local was = index == 1 and under or run.materials[index - 1]
        if was ~= run.materials[index] then
            writeCell(run.x, row, run.z, run.materials[index])
        end
    end
    writeCell(run.x, run.top, run.z, leftBehind(run.x, run.top, run.z))
    run.bottom = below
    run.top = run.top - 1
    return true
end

local function save()
    local parts = {}
    for _, run in pairs(runs) do
        parts[#parts + 1] = run.x .. "," .. run.z
    end
    table.sort(parts)
    workspace:SetAttribute("BlockWorldFalling", table.concat(parts, ";"))
end

-- Starts the fall of the lowest run of the pillar that has air or water under it.
local function startInPillar(x, z)
    local pillar = readBox(x, BEDROCK_ROW, z, x, TOP_ROW, z)[1]
    for index = 2, #pillar do
        if FALLS[pillar[index][1]] and open(pillar[index - 1][1]) then
            start(x, BEDROCK_ROW + index - 1, z)
            return
        end
    end
end

-- WHY picked up again on load: a world saved in the middle of a fall must not keep sand hanging in the air.
local saved = workspace:GetAttribute("BlockWorldFalling")
if type(saved) == "string" then
    for x, z in saved:gmatch("(-?%d+),(-?%d+)") do
        startInPillar(tonumber(x), tonumber(z))
    end
end

-- One slab (one row of z) of the watched box round the player each frame; the lowest open-under cell of falling
-- material in each pillar starts a fall.
local function sweep()
    local character = localCharacter()
    local root = character and character:FindFirstChild("HumanoidRootPart")
    local camera = workspace.CurrentCamera
    local centre = root and root.Position or (camera and camera.CFrame.Position)
    if not centre then
        return
    end
    local cx, cz = math.floor(centre.X / CELL), math.floor(centre.Z / CELL)
    local feet = math.floor((root and bodySpan(character) or centre.Y) / CELL)
    local y0 = math.max(feet - WATCH_BELOW, BEDROCK_ROW + 1)
    local y1 = math.min(feet + WATCH_ABOVE, TOP_ROW)
    -- WHY: a player flying high above the land (or deep under it) has no rows of the world to watch.
    if y1 < y0 then
        return
    end
    local z = cz - WATCH + sweepRow
    sweepRow = (sweepRow + 1) % (WATCH * 2 + 1)
    local slab = readBox(cx - WATCH, y0 - 1, z, cx + WATCH, y1, z)
    for ix = 1, #slab do
        local pillar = slab[ix]
        for iy = 2, #pillar do
            if FALLS[pillar[iy][1]] and open(pillar[iy - 1][1]) then
                start(cx - WATCH + ix - 1, y0 - 2 + iy, z)
                break
            end
        end
    end
end

RunService.Heartbeat:Connect(function(dt)
    sweep()
    local moves = 0
    for key, run in pairs(runs) do
        run.wait = run.wait + dt
        while run.wait >= STEP_SECONDS and moves < MOVES_PER_FRAME do
            run.wait = run.wait - STEP_SECONDS
            moves = moves + 1
            if not move(run) then
                runs[key] = nil
                runCount = runCount - 1
                dirty = true
                break
            end
        end
    end
    if dirty then
        dirty = false
        save()
    end
end)
