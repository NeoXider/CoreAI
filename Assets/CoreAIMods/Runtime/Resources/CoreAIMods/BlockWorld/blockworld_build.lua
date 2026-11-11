--[[@coreai
id: blockworld_build
name: Block World 2 - Build
version: 1.0.0
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo. Breaking and placing blocks at the crosshair: a thin outline shows the block you look at, left click breaks it, right click places the hotbar block on the face you look at, holding a button repeats, and nothing happens beyond a reach of five blocks. Bedrock cannot be broken and a block is never placed inside you. G switches between creative (endless blocks) and survival (you place what you broke); V switches first and third person. Publishes the crosshair cell as workspace attributes (CrosshairCell, CrosshairPlaceCell, CrosshairNormal) so the AI and other mods build where you look.
]]

-- The target is found once per frame: a ray from the camera along its look direction (the crosshair is the
-- screen centre), cut at the reach measured from the character's eyes. A Terrain hit names two cells: the
-- one hit (the ray's end, half a cell back along the face normal) and the empty one in front of that face.
-- The outline is drawn at the target and every click acts on that same stored target, so what the outline
-- shows is exactly what a click changes. A Part hit (a torch, a villager) is published by name instead, for
-- the mod that owns that part.
--
-- Reads: BlockWorldSelected (the hotbar item; set by blockworld_hud, Grass when nobody set it).
-- Writes: CrosshairHasTarget, CrosshairCell, CrosshairPlaceCell, CrosshairNormal, CrosshairPartName, CrosshairModelName,
--         BlockWorldCreative, BlockWorldPlaced, BlockWorldBroken, BlockWorldCount_<Material>, BlockWorldNotice.

local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")

local terrain = workspace.Terrain

local CELL = 4
local REACH = 20                -- studs from the eyes to the face you point at: five blocks
local BEDROCK_ROW = -16         -- the world's floor (blockworld_world)
local REPEAT_SECONDS = 0.25     -- a held button acts again this often
local EDGE = 0.1                -- outline thickness in studs
local DEFAULT_ITEM = "Grass"

-- Every Terrain material a player may place, by its Enum.Material name.
local PLACEABLE = {}
for _, name in ipairs({ "Grass", "LeafyGrass", "Ground", "Mud", "Rock", "Slate", "Basalt", "CrackedLava",
    "Sand", "Sandstone", "Snow", "Ice", "Glacier", "Salt", "Limestone", "Brick", "Cobblestone", "Concrete",
    "Pavement", "Asphalt", "WoodPlanks", "Water" }) do
    PLACEABLE[name] = Enum.Material[name]
end

local creative = workspace:GetAttribute("BlockWorldCreative")
if type(creative) ~= "boolean" then
    creative = true
end
workspace:SetAttribute("BlockWorldCreative", creative)

local counts = {}
local placed, broken = 0, 0
local target = nil
local shownKey = nil

local function notify(text)
    workspace:SetAttribute("BlockWorldNotice", text)
    workspace:SetAttribute("BlockWorldNoticeAt", (workspace:GetAttribute("BlockWorldNoticeAt") or 0) + 1)
end

local function localPlayer()
    return Players.LocalPlayer or Players:GetPlayers()[1]
end

local function localCharacter()
    local player = localPlayer()
    return player and player.Character
end

-- ---- the outline: twelve thin edges around the target box ---------------------------------------

local outline = Instance.new("Folder")
outline.Name = "BlockWorldOutline"
outline.Parent = workspace

local edges = {}
for index = 1, 12 do
    local edge = Instance.new("Part")
    edge.Name = "OutlineEdge"
    edge.Anchored = true
    edge.CanCollide = false
    edge.Material = Enum.Material.SmoothPlastic
    edge.Color = Color3.fromRGB(18, 18, 22)
    edge.Transparency = 1
    edge.Size = Vector3.new(EDGE, EDGE, EDGE)
    edge.Parent = outline
    edges[index] = edge
end

local SIGNS = { -1, 1 }

local function showBox(center, size)
    local half = size / 2
    local index = 0
    local function put(offset, edgeSize)
        index = index + 1
        local edge = edges[index]
        edge.Size = edgeSize
        edge.Position = center + offset
        edge.Transparency = 0
    end
    for _, a in ipairs(SIGNS) do
        for _, b in ipairs(SIGNS) do
            put(Vector3.new(0, a * half.Y, b * half.Z), Vector3.new(size.X + EDGE, EDGE, EDGE))
            put(Vector3.new(a * half.X, 0, b * half.Z), Vector3.new(EDGE, size.Y + EDGE, EDGE))
            put(Vector3.new(a * half.X, b * half.Y, 0), Vector3.new(EDGE, EDGE, size.Z + EDGE))
        end
    end
end

local function hideBox()
    for _, edge in ipairs(edges) do
        edge.Transparency = 1
    end
end

-- ---- finding the target --------------------------------------------------------------------------

local rayParams = RaycastParams.new()
rayParams.FilterType = Enum.RaycastFilterType.Exclude

local function eyePosition(camera)
    local character = localCharacter()
    local head = character and character:FindFirstChild("Head")
    if head then
        return head.Position
    end
    local root = character and character:FindFirstChild("HumanoidRootPart")
    if root then
        return root.Position + Vector3.new(0, 1.5, 0)
    end
    return camera.CFrame.Position
end

local function cellKey(cell)
    return cell.X .. "," .. cell.Y .. "," .. cell.Z
end

local function findTarget()
    local camera = workspace.CurrentCamera
    if not camera then
        return nil
    end
    local origin = camera.CFrame.Position
    local eye = eyePosition(camera)
    local character = localCharacter()
    rayParams.FilterDescendantsInstances = character and { outline, character } or { outline }
    local hit = workspace:Raycast(origin, camera.CFrame.LookVector * (REACH + (origin - eye).Magnitude), rayParams)
    if not hit or (hit.Position - eye).Magnitude > REACH then
        return nil
    end
    if hit.Instance == terrain then
        -- WHY a step back along the ray: a hit on a cell's edge lies on the boundary of two cells, and the normal
        -- alone can name the neighbour across that edge, whose place cell is solid. A point just before the hit
        -- is always in the empty cell in front of the face that was hit, and the face's own cell is behind it.
        local normal = Vector3.new(math.round(hit.Normal.X), math.round(hit.Normal.Y), math.round(hit.Normal.Z))
        local place = terrain:WorldToCell(hit.Position + normal * (CELL / 2) - camera.CFrame.LookVector * 0.05)
        local cell = place - normal
        return {
            kind = "Terrain",
            cell = cell,
            place = place,
            normal = hit.Normal,
            key = "T" .. cellKey(cell),
        }
    end
    return { kind = "Part", part = hit.Instance, key = "P" .. hit.Instance:GetFullName() }
end

local function publish(found)
    local onTerrain = found ~= nil and found.kind == "Terrain"
    workspace:SetAttribute("CrosshairHasTarget", onTerrain)
    if onTerrain then
        workspace:SetAttribute("CrosshairCell", found.cell)
        workspace:SetAttribute("CrosshairPlaceCell", found.place)
        workspace:SetAttribute("CrosshairNormal", found.normal)
    end
    local part = found ~= nil and found.kind == "Part" and found.part or nil
    local model = part and part:FindFirstAncestorOfClass("Model") or nil
    workspace:SetAttribute("CrosshairPartName", part and part.Name or "")
    workspace:SetAttribute("CrosshairModelName", model and model.Name or "")
end

local function refreshTarget()
    target = findTarget()
    local key = target and target.key or nil
    if key == shownKey then
        return
    end
    shownKey = key
    publish(target)
    if target == nil then
        hideBox()
    elseif target.kind == "Terrain" then
        showBox(terrain:CellCenterToWorld(target.cell.X, target.cell.Y, target.cell.Z), Vector3.new(CELL, CELL, CELL))
    else
        showBox(target.part.Position, target.part.Size)
    end
end

-- ---- breaking and placing --------------------------------------------------------------------------

local function readCell(cell)
    local corner = terrain:CellCornerToWorld(cell.X, cell.Y, cell.Z)
    local materials, occupancy = terrain:ReadVoxels(Region3.new(corner, corner + Vector3.new(CELL, CELL, CELL)), 4)
    return materials[1][1][1], occupancy[1][1][1]
end

local function writeCell(cell, material)
    terrain:FillBlock(CFrame.new(terrain:CellCenterToWorld(cell.X, cell.Y, cell.Z)), Vector3.new(CELL, CELL, CELL),
        material)
end

local function publishCount(name)
    workspace:SetAttribute("BlockWorldCount_" .. name, counts[name] or 0)
end

local function overlapsCharacter(cell)
    local character = localCharacter()
    local root = character and character:FindFirstChild("HumanoidRootPart")
    if not root then
        return false
    end
    local p = root.Position
    local x0, y0, z0 = cell.X * CELL, cell.Y * CELL, cell.Z * CELL
    return p.X + 1 > x0 and p.X - 1 < x0 + CELL
        and p.Z + 1 > z0 and p.Z - 1 < z0 + CELL
        and p.Y + 2.5 > y0 and p.Y - 3 < y0 + CELL
end

local function breakTarget()
    if not target or target.kind ~= "Terrain" then
        return
    end
    local cell = target.cell
    if cell.Y <= BEDROCK_ROW then
        notify("Bedrock cannot be broken.")
        return
    end
    local material, occupancy = readCell(cell)
    if occupancy == 0 or material == Enum.Material.Water then
        return
    end
    writeCell(cell, Enum.Material.Air)
    broken = broken + 1
    workspace:SetAttribute("BlockWorldBroken", broken)
    if not creative then
        counts[material.Name] = (counts[material.Name] or 0) + 1
        publishCount(material.Name)
    end
    shownKey = nil
end

local function placeTarget()
    if not target or target.kind ~= "Terrain" then
        return
    end
    local item = workspace:GetAttribute("BlockWorldSelected") or DEFAULT_ITEM
    local material = PLACEABLE[item]
    if not material then
        return
    end
    local cell = target.place
    local current, occupancy = readCell(cell)
    if occupancy > 0 and current ~= Enum.Material.Water then
        return
    end
    if overlapsCharacter(cell) then
        notify("You are standing there.")
        return
    end
    if not creative and (counts[item] or 0) <= 0 then
        notify("No " .. item .. " left - break some to collect it, or press G for creative.")
        return
    end
    local ok, message = pcall(writeCell, cell, material)
    if not ok then
        notify("That block cannot go there: " .. tostring(message))
        return
    end
    placed = placed + 1
    workspace:SetAttribute("BlockWorldPlaced", placed)
    if not creative then
        counts[item] = counts[item] - 1
        publishCount(item)
    end
    shownKey = nil
end

-- ---- modes ------------------------------------------------------------------------------------------

-- WHY third person first: a new player sees the character in the world it stands in; V gives first person.
local firstPerson = false
local SHOULDER_DISTANCE = 12    -- studs from the camera to the character in third person

local function applyView()
    local player = localPlayer()
    if not player then
        return
    end
    pcall(function()
        if firstPerson then
            player.CameraMode = Enum.CameraMode.LockFirstPerson
        else
            player.CameraMode = Enum.CameraMode.Classic
            player.CameraMinZoomDistance = SHOULDER_DISTANCE
            player.CameraMaxZoomDistance = SHOULDER_DISTANCE
        end
    end)
end

workspace:GetAttributeChangedSignal("BlockWorldCreative"):Connect(function()
    local value = workspace:GetAttribute("BlockWorldCreative")
    if type(value) == "boolean" and value ~= creative then
        creative = value
        notify(creative and "Creative: endless blocks." or "Survival: you place what you break.")
    end
end)

-- ---- input --------------------------------------------------------------------------------------------

local breakHeld, placeHeld = false, false
local repeatTimer = 0

UserInputService.InputBegan:Connect(function(input, gameProcessed)
    if gameProcessed then
        return
    end
    if input.UserInputType == Enum.UserInputType.MouseButton1 then
        breakHeld = true
        repeatTimer = REPEAT_SECONDS
        breakTarget()
    elseif input.UserInputType == Enum.UserInputType.MouseButton2 then
        placeHeld = true
        repeatTimer = REPEAT_SECONDS
        placeTarget()
    elseif input.KeyCode == Enum.KeyCode.G then
        workspace:SetAttribute("BlockWorldCreative", not creative)
    elseif input.KeyCode == Enum.KeyCode.V then
        firstPerson = not firstPerson
        applyView()
    end
end)

UserInputService.InputEnded:Connect(function(input)
    if input.UserInputType == Enum.UserInputType.MouseButton1 then
        breakHeld = false
    elseif input.UserInputType == Enum.UserInputType.MouseButton2 then
        placeHeld = false
    end
end)

RunService.Heartbeat:Connect(function(dt)
    refreshTarget()
    if breakHeld or placeHeld then
        repeatTimer = repeatTimer - dt
        if repeatTimer <= 0 then
            repeatTimer = REPEAT_SECONDS
            if breakHeld then
                breakTarget()
            else
                placeTarget()
            end
        end
    end
end)

Players.PlayerAdded:Connect(applyView)
applyView()
workspace:SetAttribute("BlockWorldPlaced", placed)
workspace:SetAttribute("BlockWorldBroken", broken)
publish(nil)
print("[blockworld] building ready - left click breaks, right click places, G creative/survival, V view.")
