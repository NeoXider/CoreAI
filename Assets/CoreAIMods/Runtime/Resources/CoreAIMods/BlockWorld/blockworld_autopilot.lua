--[[@coreai
id: blockworld_autopilot
name: Block World 7 - Autopilot
version: 1.0.0
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo. P (or /autopilot on in the chat) hands your character to the game's autopilot, and any movement key or P again takes it back. This mod tells the autopilot the block world's goals and facts (the Player attributes AutopilotGoals and AutopilotState) and carries out the goals only a block world knows - gather (walk to nearby wood or stone and break it), build (put a small plank shelter next to you) and home (walk back to the spawn) - while the autopilot itself walks for wander, follow and idle. The decision model picks the goal when the game has one, a fixed rule otherwise; the HUD banner shows the goal.
]]

-- The contract is plain Roblox attributes on the local Player, no new API: this mod writes Autopilot = true or
-- false to ask for the autopilot or end it, AutopilotGoals (the goal list, "name: description" entries split on
-- ";") and AutopilotState (facts for the model); the autopilot writes Autopilot back and the chosen goal as
-- AutopilotGoal. A goal the host does not move for is this mod's job, done a step at a time on Heartbeat.
--
-- Reads: BlockWorldSpawn (blockworld_world), the villager Models (blockworld_villagers), ClockTime.
-- Writes: on the Player Autopilot, AutopilotGoals, AutopilotState; on workspace BlockWorldAutopilot (banner),
--         BlockWorldAutopilotGathered, BlockWorldAutopilotBuilt.

local Lighting = game:GetService("Lighting")
local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")

local terrain = workspace.Terrain

local CELL = 4
local BEDROCK_ROW = -16
local REACH = 20                -- studs: the same reach a player has
local STEP_SECONDS = 0.3        -- one gather or build action this often
local STATE_SECONDS = 2         -- how often AutopilotState is refreshed
local SCAN = 3                  -- cells around the character searched for something to gather (7 x 7 x 7)
local GOALS = "wander: explore the hills nearby; follow: walk up to the nearest villager; "
    .. "gather: walk to nearby wood or stone and break it; build: put a small plank shelter next to you; "
    .. "home: walk back to the spawn, wise at night; idle: stand still"
local GATHER = { WoodPlanks = 1, LeafyGrass = 2, Rock = 3, Cobblestone = 3 }

local goal = ""
local stepTimer, stateTimer = 0, 0
local gathered, built = 0, 0
local shelter = nil             -- the build plan: an array of cells still to place

local function localPlayer()
    return Players.LocalPlayer or Players:GetPlayers()[1]
end

local function characterParts()
    local player = localPlayer()
    local character = player and player.Character
    if not character then
        return nil, nil
    end
    return character:FindFirstChildOfClass("Humanoid"), character:FindFirstChild("HumanoidRootPart")
end

local function engaged()
    local player = localPlayer()
    return player ~= nil and player:GetAttribute("Autopilot") == true
end

local function isNight()
    local hours = Lighting.ClockTime
    return hours < 6 or hours >= 19
end

local function spawnPoint()
    local spawn = workspace:GetAttribute("BlockWorldSpawn")
    return typeof(spawn) == "Vector3" and spawn or Vector3.new(0, 0, 0)
end

local function readCell(x, y, z)
    local corner = terrain:CellCornerToWorld(x, y, z)
    local materials, occupancy = terrain:ReadVoxels(Region3.new(corner, corner + Vector3.new(CELL, CELL, CELL)), 4)
    return materials[1][1][1], occupancy[1][1][1]
end

local function writeCell(x, y, z, material)
    terrain:FillBlock(CFrame.new(terrain:CellCenterToWorld(x, y, z)), Vector3.new(CELL, CELL, CELL), material)
end

local function nearestVillager(from)
    local best, bestDistance = nil, math.huge
    for _, model in ipairs(workspace:GetChildren()) do
        if model:IsA("Model") and model:GetAttribute("NpcBrain") and model.PrimaryPart then
            local distance = (model.PrimaryPart.Position - from).Magnitude
            if distance < bestDistance then
                best, bestDistance = model, distance
            end
        end
    end
    return best, bestDistance
end

-- ---- what the model reads ----------------------------------------------------------------------------

local function describe()
    local _, root = characterParts()
    local here = root and root.Position or spawnPoint()
    local villager, distance = nearestVillager(here)
    local clock = math.floor(Lighting.ClockTime * 60 + 0.5) % 1440
    return (isNight() and "It is night" or "It is day") .. " (" .. math.floor(clock / 60) .. ":"
        .. string.format("%02d", clock % 60) .. "). "
        .. "The spawn is " .. math.floor((here - spawnPoint()).Magnitude) .. " studs away. "
        .. (villager and ("The nearest villager, " .. villager.Name .. ", is " .. math.floor(distance) .. " studs away. ")
            or "No villager is near. ")
        .. "Gathered " .. gathered .. " blocks, built " .. built .. "."
end

local function publishContract(player)
    if player then
        player:SetAttribute("AutopilotGoals", GOALS)
        player:SetAttribute("AutopilotState", describe())
    end
end

-- ---- the goals only a block world knows ----------------------------------------------------------------

local function gatherStep(humanoid, root)
    local center = terrain:WorldToCell(root.Position)
    local corner = terrain:CellCornerToWorld(center.X - SCAN, center.Y - SCAN, center.Z - SCAN)
    local region = Region3.new(corner, corner + Vector3.new(CELL, CELL, CELL) * (2 * SCAN + 1))
    local materials, occupancy = terrain:ReadVoxels(region, 4)
    local best, bestRank, bestDistance = nil, math.huge, math.huge
    for x = 1, 2 * SCAN + 1 do
        local mx, ox = materials[x], occupancy[x]
        for y = 1, 2 * SCAN + 1 do
            local my, oy = mx[y], ox[y]
            for z = 1, 2 * SCAN + 1 do
                local rank = oy[z] > 0 and GATHER[my[z].Name]
                if rank and rank <= bestRank then
                    local distance = (x - SCAN - 1) ^ 2 + (y - SCAN - 1) ^ 2 + (z - SCAN - 1) ^ 2
                    if rank < bestRank or distance < bestDistance then
                        best, bestRank, bestDistance = { center.X + x - SCAN - 1, center.Y + y - SCAN - 1,
                            center.Z + z - SCAN - 1 }, rank, distance
                    end
                end
            end
        end
    end
    if not best or best[2] <= BEDROCK_ROW then
        return
    end
    local target = terrain:CellCenterToWorld(best[1], best[2], best[3])
    if (target - root.Position).Magnitude > REACH then
        humanoid:MoveTo(target)
        return
    end
    writeCell(best[1], best[2], best[3], Enum.Material.Air)
    gathered = gathered + 1
    workspace:SetAttribute("BlockWorldAutopilotGathered", gathered)
end

-- A 3 x 3 ring of planks, two blocks high, with a doorway, beside the character, on the first free row.
local function planShelter(root)
    local look = root.CFrame.LookVector
    local dx, dz = 0, 0
    if math.abs(look.X) > math.abs(look.Z) then
        dx = math.sign(look.X)
    else
        dz = look.Z >= 0 and 1 or -1
    end
    local feet = terrain:WorldToCell(root.Position - Vector3.new(0, 2.5, 0))
    local cx, cz = feet.X + dx * 3, feet.Z + dz * 3
    local floor = feet.Y
    for _ = 1, 6 do
        local _, occupancy = readCell(cx, floor, cz)
        if occupancy == 0 then
            break
        end
        floor = floor + 1
    end
    local plan = {}
    for y = floor, floor + 1 do
        for ox = -1, 1 do
            for oz = -1, 1 do
                local edge = ox ~= 0 or oz ~= 0
                local door = ox == -dx and oz == -dz
                if edge and not door then
                    plan[#plan + 1] = { cx + ox, y, cz + oz }
                end
            end
        end
    end
    return plan
end

local function buildStep(root)
    shelter = shelter or planShelter(root)
    while #shelter > 0 do
        local cell = table.remove(shelter, 1)
        local _, occupancy = readCell(cell[1], cell[2], cell[3])
        if occupancy == 0 then
            writeCell(cell[1], cell[2], cell[3], Enum.Material.WoodPlanks)
            built = built + 1
            workspace:SetAttribute("BlockWorldAutopilotBuilt", built)
            return
        end
    end
end

-- ---- hand-over and the goal loop ---------------------------------------------------------------------------

local function showBanner()
    workspace:SetAttribute("BlockWorldAutopilot", engaged() and goal ~= "" and goal or (engaged() and "on" or ""))
end

local function onGoal()
    local player = localPlayer()
    goal = player and player:GetAttribute("AutopilotGoal") or ""
    shelter = nil
    stepTimer = 0
    showBanner()
end

local function watch(player)
    publishContract(player)
    player:GetAttributeChangedSignal("AutopilotGoal"):Connect(onGoal)
    player:GetAttributeChangedSignal("Autopilot"):Connect(function()
        if not engaged() then
            goal, shelter = "", nil
        end
        showBanner()
    end)
end

for _, player in ipairs(Players:GetPlayers()) do
    watch(player)
end
Players.PlayerAdded:Connect(watch)

UserInputService.InputBegan:Connect(function(input, gameProcessed)
    if gameProcessed or input.KeyCode ~= Enum.KeyCode.P then
        return
    end
    local player = localPlayer()
    if player then
        player:SetAttribute("Autopilot", not engaged())
    end
end)

RunService.Heartbeat:Connect(function(dt)
    stateTimer = stateTimer - dt
    if stateTimer <= 0 then
        stateTimer = STATE_SECONDS
        local player = localPlayer()
        if player then
            player:SetAttribute("AutopilotState", describe())
        end
    end
    if not engaged() then
        return
    end
    stepTimer = stepTimer - dt
    if stepTimer > 0 then
        return
    end
    stepTimer = STEP_SECONDS
    local humanoid, root = characterParts()
    if not humanoid or not root then
        return
    end
    if goal == "gather" then
        gatherStep(humanoid, root)
    elseif goal == "build" then
        buildStep(root)
    elseif goal == "home" and (spawnPoint() - root.Position).Magnitude > 6 then
        humanoid:MoveTo(spawnPoint())
    end
end)

showBanner()
print("[blockworld] autopilot goals published - press P or type /autopilot on to hand over the controls.")
