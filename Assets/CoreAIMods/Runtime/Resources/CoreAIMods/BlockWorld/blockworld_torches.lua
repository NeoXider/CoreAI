--[[@coreai
id: blockworld_torches
name: Block World 5 - Torches
version: 1.2.0
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo. With the torch in your hand (slot 9), right click puts a torch on the floor or a wall you look at - upright on the middle of a floor, or leaning out from a wall with its foot on the wall, as in a block game; each carries a flickering PointLight and a glowing flame, so caves and nights can be lit. Left click on a torch takes it back, and a torch whose block is broken drops. The light budget is explicit - at most 48 lit torches from this mod, and a notice says so when it is full; on top of that the host's own light budget applies (desktop Forward+ keeps 64 lights, the WebGL tier keeps the 8 nearest on and reports it).
]]

-- Torches are Parts, not Terrain cells: a cell holds only a material, while a torch needs a light and a flame.
-- Where to put one comes from the crosshair attributes blockworld_build publishes (the cell in front of the face
-- you look at, and that face's normal), so the outline you see is where the torch goes. A torch is found again by
-- its part name, which the crosshair also publishes.

local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")

local terrain = workspace.Terrain

local CELL = 4
local MAX_LIT = 48              -- the mod's light budget: PointLights it keeps at once
local CHECKS_PER_FRAME = 4      -- torches whose support is checked each frame
local FLICKERS_PER_FRAME = 12   -- torches whose flame flickers each frame
local LIGHT_RANGE = 26
local LIGHT_BRIGHTNESS = 2.2
local LIGHT_COLOR = Color3.fromRGB(255, 196, 120)
local STICK_LENGTH = 2.2
local STICK_WIDTH = 0.4
local FLAME_SIZE = 0.6
local WALL_LEAN = math.rad(22.5) -- a wall torch leans this far out from the wall
local WALL_FOOT = 1             -- studs above the cell's floor where a wall torch's foot meets the wall

local torches = {}              -- array of { cell, support, stick, flame, light, name }
local byName = {}               -- part name -> torch
local byCell = {}               -- "x_y_z" -> torch

local function notify(text)
    workspace:SetAttribute("BlockWorldNotice", text)
    workspace:SetAttribute("BlockWorldNoticeAt", (workspace:GetAttribute("BlockWorldNoticeAt") or 0) + 1)
end

local function cellName(cell)
    return cell.X .. "_" .. cell.Y .. "_" .. cell.Z
end

-- WHY adopt: a saved world brings its torches back in this folder; a second folder would duplicate them.
local folder = workspace:FindFirstChild("BlockWorldTorches")
local adopting = folder ~= nil
if not folder then
    folder = Instance.new("Folder")
    folder.Name = "BlockWorldTorches"
    folder.Parent = workspace
end

-- WHY the cells on the Workspace: a saved world keeps the Workspace's attributes but not a mod's parts, so the torches
-- come back from this list (BlockWorldTorchCells, "x,y,z,nx,ny,nz;..."), and CoreAIWorldEdits tells a world kept between
-- starts that a torch changed, as a block does.
local restoring = false

local function publish()
    workspace:SetAttribute("BlockWorldTorches", #torches)
    local cells = {}
    for index, torch in ipairs(torches) do
        local normal = torch.cell - torch.support
        cells[index] = torch.cell.X .. "," .. torch.cell.Y .. "," .. torch.cell.Z .. ","
            .. normal.X .. "," .. normal.Y .. "," .. normal.Z
    end
    workspace:SetAttribute("BlockWorldTorchCells", table.concat(cells, ";"))
    if not restoring then
        workspace:SetAttribute("CoreAIWorldEdits", (workspace:GetAttribute("CoreAIWorldEdits") or 0) + 1)
    end
end

local function readOccupancy(cell)
    local corner = terrain:CellCornerToWorld(cell.X, cell.Y, cell.Z)
    local materials, occupancy = terrain:ReadVoxels(Region3.new(corner, corner + Vector3.new(CELL, CELL, CELL)), 4)
    return occupancy[1][1][1], materials[1][1][1]
end

local function newPart(name, size, position, material, color)
    local part = Instance.new("Part")
    part.Name = name
    part.Anchored = true
    part.CanCollide = false
    part.Size = size
    part.Position = position
    part.Material = material
    part.Color = color
    part.Parent = folder
    return part
end

local function addTorch(cell, support, stick, flame, light)
    local torch = { cell = cell, support = support, stick = stick, flame = flame, light = light, name = stick.Name,
        phase = #torches * 1.7 }
    torches[#torches + 1] = torch
    byCell[cellName(cell)] = torch
    byName[stick.Name] = torch
    byName[flame.Name] = torch
    publish()
    return torch
end

local function placeTorch(cell, normal)
    local key = cellName(cell)
    if byCell[key] then
        return nil
    end
    if normal.Y < -0.5 then
        notify("Torches stand on floors and walls, not ceilings.")
        return nil
    end
    if #torches >= MAX_LIT then
        notify("Torch budget reached: " .. MAX_LIT .. " lit torches. Take one back (left click on it) to place another.")
        return nil
    end
    local occupancy, material = readOccupancy(cell)
    if occupancy > 0 and material ~= Enum.Material.Water then
        return nil
    end

    -- WHY a foot and an axis: on a floor the stick stands on the middle of the cell's floor, straight up; on a wall its
    -- foot is on the wall's face and it leans out by WALL_LEAN, so it reads as fixed to the wall, not hanging by it.
    local center = terrain:CellCenterToWorld(cell.X, cell.Y, cell.Z)
    local foot, stickCFrame
    if normal.Y > 0.5 then
        foot = center - Vector3.new(0, CELL / 2, 0)
        stickCFrame = CFrame.new(foot + Vector3.new(0, STICK_LENGTH / 2, 0))
    else
        local outward = Vector3.new(normal.X, 0, normal.Z).Unit
        foot = center - outward * (CELL / 2) + Vector3.new(0, WALL_FOOT - CELL / 2, 0)
        local axis = Vector3.new(0, 1, 0):Cross(outward)
        local tilt = CFrame.fromAxisAngle(axis, WALL_LEAN)
        stickCFrame = CFrame.new(foot) * tilt * CFrame.new(0, STICK_LENGTH / 2, 0)
    end
    local name = "Torch_" .. key
    local stick = newPart(name, Vector3.new(STICK_WIDTH, STICK_LENGTH, STICK_WIDTH), stickCFrame.Position,
        Enum.Material.WoodPlanks, Color3.fromRGB(110, 76, 44))
    stick.CFrame = stickCFrame
    -- WHY on the tip: the flame sits on the stick's end, turned with it, and carries the light.
    local flame = newPart("TorchFlame_" .. key, Vector3.new(FLAME_SIZE, FLAME_SIZE, FLAME_SIZE),
        stickCFrame.Position, Enum.Material.Neon, Color3.fromRGB(255, 170, 60))
    flame.CFrame = stickCFrame * CFrame.new(0, STICK_LENGTH / 2 + FLAME_SIZE / 2 - 0.1, 0)
    local light = Instance.new("PointLight")
    light.Range = LIGHT_RANGE
    light.Brightness = LIGHT_BRIGHTNESS
    light.Color = LIGHT_COLOR
    light.Shadows = false
    light.Parent = flame

    stick:SetAttribute("TorchCell", cell)
    stick:SetAttribute("TorchSupport", cell - normal)
    return addTorch(cell, cell - normal, stick, flame, light)
end

local function removeTorch(torch)
    for index, other in ipairs(torches) do
        if other == torch then
            table.remove(torches, index)
            break
        end
    end
    byCell[cellName(torch.cell)] = nil
    byName[torch.name] = nil
    byName[torch.flame.Name] = nil
    torch.stick:Destroy()
    torch.flame:Destroy()
    publish()
end

UserInputService.InputBegan:Connect(function(input, gameProcessed)
    if gameProcessed then
        return
    end
    if input.UserInputType == Enum.UserInputType.MouseButton1 then
        local torch = byName[workspace:GetAttribute("CrosshairPartName") or ""]
        if torch then
            removeTorch(torch)
        end
    elseif input.UserInputType == Enum.UserInputType.MouseButton2 then
        if workspace:GetAttribute("BlockWorldSelected") == "Torch" and workspace:GetAttribute("CrosshairHasTarget") then
            local cell = workspace:GetAttribute("CrosshairPlaceCell")
            local normal = workspace:GetAttribute("CrosshairNormal")
            if typeof(cell) == "Vector3" and typeof(normal) == "Vector3" then
                placeTorch(cell, normal)
            end
        end
    end
end)

-- A few torches per frame: drop the ones whose block is gone (or whose cell was filled), flicker the flames.
local checkIndex, flickerIndex, clock = 0, 0, 0
RunService.Heartbeat:Connect(function(dt)
    clock = clock + dt
    local count = #torches
    if count == 0 then
        return
    end
    for _ = 1, math.min(CHECKS_PER_FRAME, count) do
        checkIndex = checkIndex % #torches + 1
        local torch = torches[checkIndex]
        local supportOccupancy = readOccupancy(torch.support)
        local ownOccupancy, ownMaterial = readOccupancy(torch.cell)
        if supportOccupancy == 0 or (ownOccupancy > 0 and ownMaterial ~= Enum.Material.Water) then
            removeTorch(torch)
            if #torches == 0 then
                return
            end
        end
    end
    for _ = 1, math.min(FLICKERS_PER_FRAME, #torches) do
        flickerIndex = flickerIndex % #torches + 1
        local torch = torches[flickerIndex]
        local wave = math.noise(clock * 2.3, torch.phase, 0.5)
        torch.light.Brightness = LIGHT_BRIGHTNESS + wave * 0.6
    end
end)

restoring = true
if adopting then
    for _, stick in ipairs(folder:GetChildren()) do
        local cell, support = stick:GetAttribute("TorchCell"), stick:GetAttribute("TorchSupport")
        local flame = folder:FindFirstChild("TorchFlame_" .. string.sub(stick.Name, 7))
        local light = flame and flame:FindFirstChildOfClass("PointLight")
        if typeof(cell) == "Vector3" and typeof(support) == "Vector3" and light and #torches < MAX_LIT then
            addTorch(cell, support, stick, flame, light)
        end
    end
end

-- A saved world's torches: placed again from the list the last session published (a reload of this mod keeps its
-- folder and adopts it instead).
local saved = workspace:GetAttribute("BlockWorldTorchCells")
if not adopting and type(saved) == "string" then
    for entry in string.gmatch(saved, "[^;]+") do
        local x, y, z, nx, ny, nz = string.match(entry, "^(-?%d+),(-?%d+),(-?%d+),(-?%d+),(-?%d+),(-?%d+)$")
        if x then
            placeTorch(Vector3.new(tonumber(x), tonumber(y), tonumber(z)),
                Vector3.new(tonumber(nx), tonumber(ny), tonumber(nz)))
        end
    end
end

publish()
restoring = false
print("[blockworld] torches ready - choose slot 9 and right click a floor or a wall (budget " .. MAX_LIT .. ").")
