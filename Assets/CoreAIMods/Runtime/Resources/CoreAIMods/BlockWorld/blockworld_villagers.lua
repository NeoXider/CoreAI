--[[@coreai
id: blockworld_villagers
name: Block World 6 - Villagers
version: 1.2.0
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo. Three villagers live by the spawn - Mira the baker, Tobin the builder and Elda the fisher - each a character with Roblox's R6 body (Head, Torso, arms and legs) that the platform animates, created by this mod as an AIService agent with a name, a persona and a memory of its own, and given a brain (NpcAgent and NpcBrain on its Model) that lets it wander, greet, face you, follow you and go home. Talk to them in the chat by name ("Tobin, what should I build?") or look at one and press E; they answer in the chat and in a bubble over their heads. They need the game to allow AI from Lua (CoreAiLuaAiOptions.AllowAgentsFromLua) and a model: without the setting they stand still and say why, without a model they answer with a line that says one is needed.
]]

-- A villager is a Roblox character: a Model directly under the Workspace with a HumanoidRootPart, a Humanoid and
-- the six R6 parts, so the host's NPC brain can walk it (wander, go_home, follow_player) and turn it (face_player,
-- at command time), and the platform's character rig animates it (idle, walk, jump, fall) as it does the player.
-- The rig owns the six parts: they are unanchored and never collide, and the root (an invisible 2x2x1 box resting
-- on the floor) is the only collider. The hair is this mod's own and follows the Head after the rig has posed it.
-- The agent comes from AIService:CreateAgent, which only a game that turned AI from
-- Lua on allows; a refusal is caught, its reason kept, and every greeting then explains it. Chat lines reach the
-- agents through the host (a line that names a villager, follows up, or is said within its hearing radius goes to
-- that villager); E is the in-world way to start a conversation.
--
-- Reads: BlockWorldReady, BlockWorldSpawn (blockworld_world), CrosshairModelName (blockworld_build),
--        BlockWorldPlaced, BlockWorldBroken, BlockWorldTorches, Lighting.ClockTime (for NpcState).
-- Writes: on each villager Model NpcAgent, NpcBrain and NpcState; on workspace BlockWorldVillagerReply
--        ("Name: text" of the last answer) and BlockWorldNotice.

local Lighting = game:GetService("Lighting")
local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")

local terrain = workspace.Terrain

local TALK_DISTANCE = 14        -- studs: E reaches the nearest villager this close
local FACE_DISTANCE = 18        -- studs: a villager with no brain turns to a player this close
local PLACE_TIMEOUT = 2         -- seconds to wait for the ground under a villager before using the spawn height
local STATE_SECONDS = 5         -- how often the villagers' facts (NpcState) are refreshed
local BRAIN = "idle, wander, greet, face_player, follow_player, go_home"

-- Homes around the spawn in studs, in the frame of the heading a new player first faces (BlockWorldSpawnLook):
-- `ahead` along it, `right` across it. Mira stands in that first view, a little to the left; Tobin and Elda are
-- found by turning round.
local VILLAGERS = {
    {
        name = "Mira", ahead = 14, right = -4, shirt = Color3.fromRGB(204, 74, 62),
        trousers = Color3.fromRGB(96, 66, 50), skin = Color3.fromRGB(238, 198, 162), hair = Color3.fromRGB(92, 52, 34),
        persona = "Mira, the village baker of a little block village by the sea. Warm, busy and proud of her "
            .. "brick oven; she dreams of a bigger bakery made of Brick with a Cobblestone chimney.",
        voice = "Two short sentences at most. Calls the player by name. Mentions bread or the oven now and then.",
        fallback = "Mira smiles but says nothing: no AI model answered. Pick a model in the Hub so the villagers can talk.",
    },
    {
        name = "Tobin", ahead = 4, right = 16, shirt = Color3.fromRGB(64, 116, 200),
        trousers = Color3.fromRGB(48, 54, 70), skin = Color3.fromRGB(196, 146, 108), hair = Color3.fromRGB(34, 28, 26),
        persona = "Tobin, the village builder. Loves tall watchtowers, straight walls and WoodPlanks; always "
            .. "suggests something to build next and where to put it.",
        voice = "Short, practical, enthusiastic. Talks in blocks and cells. Calls the player by name.",
        fallback = "Tobin scratches his head: no AI model answered. Pick a model in the Hub so the villagers can talk.",
    },
    {
        name = "Elda", ahead = -14, right = -6, shirt = Color3.fromRGB(78, 156, 96),
        trousers = Color3.fromRGB(72, 82, 96), skin = Color3.fromRGB(226, 190, 170), hair = Color3.fromRGB(200, 200, 204),
        persona = "Elda, an old fisher who knows the shore, the sea and the weather; she warns about the dark and "
            .. "tells players to carry torches at night.",
        voice = "Calm, a little mysterious, two sentences at most. Calls the player by name.",
        fallback = "Elda looks at the sea: no AI model answered. Pick a model in the Hub so the villagers can talk.",
    },
}

local function notify(text)
    workspace:SetAttribute("BlockWorldNotice", text)
    workspace:SetAttribute("BlockWorldNoticeAt", (workspace:GetAttribute("BlockWorldNoticeAt") or 0) + 1)
end

local function localPlayer()
    return Players.LocalPlayer or Players:GetPlayers()[1]
end

local function rootOf(player)
    local character = player and player.Character
    return character and character:FindFirstChild("HumanoidRootPart")
end

-- ---- bodies ----------------------------------------------------------------------------------------

local function newPart(model, name, size, color, anchored)
    local part = Instance.new("Part")
    part.Name = name
    part.Anchored = anchored
    part.CanCollide = false
    part.Size = size
    part.Color = color
    part.Material = Enum.Material.SmoothPlastic
    part.Parent = model
    return part
end

-- Roblox's R6 body, laid out as the rig rests it: the root's box on the floor, the Torso 2 studs above it, the Head
-- 3.5. The Head is the size Roblox draws its head mesh at (a 1.25-stud block, narrower than the shoulders); the
-- colours are the villager's own ("shirt", "trousers", "skin").
local BODY = {
    { name = "Torso", size = Vector3.new(2, 2, 1), offset = Vector3.new(0, 2, 0), color = "shirt" },
    { name = "Head", size = Vector3.new(1.25, 1.25, 1.25), offset = Vector3.new(0, 3.5, 0), color = "skin" },
    { name = "Left Arm", size = Vector3.new(1, 2, 1), offset = Vector3.new(-1.5, 2, 0), color = "shirt" },
    { name = "Right Arm", size = Vector3.new(1, 2, 1), offset = Vector3.new(1.5, 2, 0), color = "shirt" },
    { name = "Left Leg", size = Vector3.new(1, 2, 1), offset = Vector3.new(-0.5, 0, 0), color = "trousers" },
    { name = "Right Leg", size = Vector3.new(1, 2, 1), offset = Vector3.new(0.5, 0, 0), color = "trousers" },
}
local HAIR_SIZE = Vector3.new(1.35, 0.3, 1.35)
local HAIR_ON_HEAD = CFrame.new(0, 0.7, 0)
-- Two eyes on the Head's front (a character faces -Z), so a villager reads as a person and shows where it looks.
local EYE_SIZE = Vector3.new(0.2, 0.26, 0.05)
local EYE_COLOR = Color3.fromRGB(28, 30, 38)
local EYES = { CFrame.new(-0.27, 0.1, -0.64), CFrame.new(0.27, 0.1, -0.64) }

local function buildBody(villager)
    local model = Instance.new("Model")
    model.Name = villager.name
    -- WHY anchored at first: the root is held until the ground under its home exists (see tryPlace), then let go.
    local root = newPart(model, "HumanoidRootPart", Vector3.new(2, 2, 1), villager.shirt, true)
    root.Transparency = 1
    root.CFrame = CFrame.new()
    for _, piece in ipairs(BODY) do
        newPart(model, piece.name, piece.size, villager[piece.color], false).CFrame = CFrame.new(piece.offset)
    end
    villager.head = model:FindFirstChild("Head")
    villager.extras = { { part = newPart(model, "Hair", HAIR_SIZE, villager.hair, true), offset = HAIR_ON_HEAD } }
    for index, offset in ipairs(EYES) do
        villager.extras[#villager.extras + 1] = { part = newPart(model, "Eye" .. index, EYE_SIZE, EYE_COLOR, true),
            offset = offset }
    end
    for _, extra in ipairs(villager.extras) do
        extra.part.CFrame = villager.head.CFrame * extra.offset
    end
    local humanoid = Instance.new("Humanoid")
    humanoid.WalkSpeed = 8
    humanoid.Parent = model
    model.PrimaryPart = root
    model.Parent = workspace
    villager.model, villager.root = model, root
end

-- The hair and the eyes are not R6 parts, so the rig leaves them alone: they follow the Head from RenderStepped,
-- which runs after the rig has posed the body this frame. A head that has not moved is not written again.
local function followHead(villager)
    local cframe = villager.head.CFrame
    if villager.headAt ~= cframe then
        villager.headAt = cframe
        for _, extra in ipairs(villager.extras) do
            extra.part.CFrame = cframe * extra.offset
        end
    end
end

-- ---- agents and brains --------------------------------------------------------------------------------

local silentReason = nil

local function explain(message)
    local text = tostring(message)
    if string.find(text, "AllowAgentsFromLua", 1, true) then
        return "AI from Lua is off in this game (the framework setting CoreAiLuaAiOptions.AllowAgentsFromLua)."
    end
    return text
end

local function makeAgent(villager)
    local ok, result = pcall(function()
        local AIService = game:GetService("AIService")
        return AIService:CreateAgent({
            Name = villager.name,
            Persona = villager.persona,
            VoiceRules = villager.voice,
            FallbackLines = { villager.fallback },
            Memory = "blockworld/" .. villager.name,
            Body = villager.model,
            HearingRadius = 40,
            InteractionRadius = TALK_DISTANCE,
        })
    end)
    if not ok then
        silentReason = silentReason or explain(result)
        return
    end
    villager.agent = result
    result.Replied:Connect(function(text)
        workspace:SetAttribute("BlockWorldVillagerReply", villager.name .. ": " .. tostring(text))
    end)
end

local function giveBrain(villager)
    if villager.agent then
        villager.model:SetAttribute("NpcAgent", villager.agent.Id)
        villager.model:SetAttribute("NpcBrain", BRAIN)
    end
end

for _, villager in ipairs(VILLAGERS) do
    buildBody(villager)
    makeAgent(villager)
end

if silentReason then
    notify("The villagers are silent: " .. silentReason)
    print("[blockworld] the villagers are silent: " .. silentReason)
else
    print("[blockworld] Mira, Tobin and Elda live by the spawn - say their name in the chat or look at one and press E.")
end

-- ---- standing on the ground ------------------------------------------------------------------------

local groundParams = RaycastParams.new()
groundParams.FilterType = Enum.RaycastFilterType.Include
groundParams.FilterDescendantsInstances = { terrain }

-- WHY only over a finished column: blockworld_world lays a column's deep stone a few frames before the rest, so
-- ground found earlier is the floor of a column still being filled, and a villager stood on it ends up inside rock.
-- The bedrock row is the world's "this column is done" mark.
local BEDROCK_ROW = -16
local function groundUnder(spot)
    local cell = terrain:WorldToCell(Vector3.new(spot.X, BEDROCK_ROW * 4 + 2, spot.Z))
    local corner = terrain:CellCornerToWorld(cell.X, BEDROCK_ROW, cell.Z)
    local materials = terrain:ReadVoxels(Region3.new(corner, corner + Vector3.new(4, 4, 4)), 4)
    if materials[1][1][1] ~= Enum.Material.Basalt then
        return nil
    end
    return workspace:Raycast(Vector3.new(spot.X, 200, spot.Z), Vector3.new(0, -400, 0), groundParams)
end

-- Stands the villager on the ground found under its home (the root's box on the floor) and lets the motor have it.
local function letGo(villager, groundY)
    villager.home = Vector3.new(villager.home.X, groundY + 1, villager.home.Z)
    villager.model:PivotTo(CFrame.new(villager.home))
    -- WHY CanCollide here: the root is the villager's only collider, made without collision while it was held;
    -- let go without it, the villager falls through the Terrain.
    villager.root.CanCollide = true
    villager.root.Anchored = false
    villager.grounded = true
end

-- The root is held (anchored) until the ground under it exists; then the brain and the motor take over. After the
-- timeout the villager is shown at the spawn height and its brain is given, still held, until its column is made.
local function tryPlace(villager, force)
    local spawn = workspace:GetAttribute("BlockWorldSpawn")
    if typeof(spawn) ~= "Vector3" then
        return
    end
    local look = workspace:GetAttribute("BlockWorldSpawnLook")
    local ahead = typeof(look) == "Vector3" and Vector3.new(look.X, 0, look.Z) or Vector3.zero
    ahead = ahead.Magnitude > 0.01 and ahead.Unit or Vector3.new(0, 0, -1)
    local right = ahead:Cross(Vector3.new(0, 1, 0))
    local spot = spawn + ahead * villager.ahead + right * villager.right
    local hit = groundUnder(spot)
    if not hit and not force then
        return
    end
    -- WHY 3 under the spawn: BlockWorldSpawn is 4 studs above the spawn's ground, the root's centre 1.
    villager.home = Vector3.new(spot.X, spawn.Y - 3, spot.Z)
    villager.model:PivotTo(CFrame.new(villager.home))
    if hit then
        letGo(villager, hit.Position.Y)
    end
    giveBrain(villager)
end

-- A held villager is let go once its ground exists; one that fell out of the world is held at home again.
local function keepOnGround(villager)
    if villager.grounded then
        if villager.root.Position.Y < villager.home.Y - 40 then
            villager.root.Anchored = true
            villager.model:PivotTo(CFrame.new(villager.home))
            villager.grounded = false
        end
        return
    end
    local hit = groundUnder(villager.home)
    if hit then
        letGo(villager, hit.Position.Y)
    end
end

-- ---- talking ----------------------------------------------------------------------------------------

local function villagerNamed(name)
    for _, villager in ipairs(VILLAGERS) do
        if villager.name == name then
            return villager
        end
    end
    return nil
end

local function nearestVillager()
    local root = rootOf(localPlayer())
    if not root then
        return nil
    end
    local best, bestDistance = nil, TALK_DISTANCE
    for _, villager in ipairs(VILLAGERS) do
        if villager.home then
            local distance = (villager.root.Position - root.Position).Magnitude
            if distance < bestDistance then
                best, bestDistance = villager, distance
            end
        end
    end
    return best
end

local function greet(villager)
    if not villager.agent then
        notify(villager.name .. " is silent: " .. (silentReason or "no agent"))
        return
    end
    local player = localPlayer()
    local speaker = player and player.Name or "Player"
    task.spawn(function()
        villager.agent:Ask(speaker .. " walks up to you and says hello.", speaker)
    end)
end

UserInputService.InputBegan:Connect(function(input, gameProcessed)
    if gameProcessed or input.KeyCode ~= Enum.KeyCode.E then
        return
    end
    local villager = villagerNamed(workspace:GetAttribute("CrosshairModelName")) or nearestVillager()
    if villager then
        greet(villager)
    end
end)

-- ---- every frame: find the ground, keep the hair on, tell the brains what is going on ----------------------

RunService.RenderStepped:Connect(function()
    for _, villager in ipairs(VILLAGERS) do
        followHead(villager)
    end
end)

local function facts()
    local hours = Lighting.ClockTime
    local daytime = (hours < 6 or hours >= 19) and "It is night; the dark is dangerous without torches." or "It is day."
    return daytime .. " The player has placed " .. (workspace:GetAttribute("BlockWorldPlaced") or 0) .. " blocks and broken "
        .. (workspace:GetAttribute("BlockWorldBroken") or 0) .. "; " .. (workspace:GetAttribute("BlockWorldTorches") or 0)
        .. " torches are lit."
end

local waited, faceTimer, stateTimer = 0, 0, 0
RunService.Heartbeat:Connect(function(dt)
    if workspace:GetAttribute("BlockWorldReady") ~= true then
        return
    end
    waited = waited + dt
    for _, villager in ipairs(VILLAGERS) do
        if villager.home then
            keepOnGround(villager)
        else
            tryPlace(villager, waited > PLACE_TIMEOUT)
        end
    end

    stateTimer = stateTimer - dt
    if stateTimer <= 0 then
        stateTimer = STATE_SECONDS
        local text = facts()
        for _, villager in ipairs(VILLAGERS) do
            if villager.agent then
                villager.model:SetAttribute("NpcState", text)
            end
        end
    end

    -- Without a brain (no agent) a villager still turns to a player who comes close.
    faceTimer = faceTimer - dt
    if faceTimer > 0 or not silentReason then
        return
    end
    faceTimer = 0.1
    local root = rootOf(localPlayer())
    if not root then
        return
    end
    local target = root.Position
    for _, villager in ipairs(VILLAGERS) do
        local at = villager.home and villager.root.Position
        if at and (target - at).Magnitude < FACE_DISTANCE then
            villager.model:PivotTo(CFrame.lookAt(at, Vector3.new(target.X, at.Y, target.Z)))
        end
    end
end)
