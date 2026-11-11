--[[@coreai
id: blockworld_survival
name: Block World 8 - Survival
version: 1.1.0
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo. Survival as a small loop on the Humanoid's own health: a fall of more than three blocks hurts and a long one kills, at zero health you die and come back at the spawn after a short message, and the HUD shows your health. Breaking a block gives it to you and placing takes it (blockworld_build keeps the counts). Creative (G) switches all of it off. Not built: mobs, hunger and crafting.
]]

-- A fall is measured from the highest point of the Humanoid's Freefall to where it lands (Landed or Running), so a
-- jump that lands higher costs nothing and flying (the Flying state) never counts. Humanoid.StateChanged says when a
-- fall starts and ends; while one lasts, Heartbeat follows the root up to its highest point. The damage goes through
-- Humanoid:TakeDamage, so Health, HealthChanged and Died are Roblox's own; the respawn is the Players service's
-- (CharacterAutoLoads after RespawnTime), and blockworld_world puts the new character at the spawn.
--
-- Reads: BlockWorldCreative (blockworld_build). Writes: BlockWorldHealth, BlockWorldMaxHealth, BlockWorldDeaths,
-- BlockWorldNotice.

local Players = game:GetService("Players")
local RunService = game:GetService("RunService")

local SAFE_FALL = 12            -- studs (three blocks) a player falls without harm
local DAMAGE_PER_STUD = 2.5     -- health per stud beyond that: 52 studs (thirteen blocks) take all 100
local RESPAWN_SECONDS = 3

local FREEFALL = Enum.HumanoidStateType.Freefall
local FLYING = Enum.HumanoidStateType.Flying
local LANDED = Enum.HumanoidStateType.Landed
local RUNNING = Enum.HumanoidStateType.Running

local deaths = 0
local watched = nil             -- the Humanoid followed now
local connections = {}
local peak = nil                -- highest root height of the fall in progress; nil when none is

local function notify(text)
    workspace:SetAttribute("BlockWorldNotice", text)
    workspace:SetAttribute("BlockWorldNoticeAt", (workspace:GetAttribute("BlockWorldNoticeAt") or 0) + 1)
end

local function creative()
    return workspace:GetAttribute("BlockWorldCreative") ~= false
end

local function publish(humanoid)
    workspace:SetAttribute("BlockWorldHealth", math.floor(humanoid.Health + 0.5))
    workspace:SetAttribute("BlockWorldMaxHealth", math.floor(humanoid.MaxHealth + 0.5))
end

local function rootHeight(humanoid)
    local root = humanoid.Parent and humanoid.Parent:FindFirstChild("HumanoidRootPart")
    return root and root.Position.Y or nil
end

local function landed(humanoid)
    local height = rootHeight(humanoid)
    local fall = peak and height and peak - height or 0
    peak = nil
    if creative() or fall <= SAFE_FALL or humanoid.Health <= 0 then
        return
    end
    local damage = (fall - SAFE_FALL) * DAMAGE_PER_STUD
    humanoid:TakeDamage(damage)
    if humanoid.Health > 0 then
        notify("Ouch - a fall of " .. math.floor(fall / 4 + 0.5) .. " blocks cost " .. math.floor(damage + 0.5)
            .. " health.")
    end
end

local function follow(character)
    for _, connection in ipairs(connections) do
        connection:Disconnect()
    end
    connections, watched, peak = {}, nil, nil
    local humanoid = character and character:FindFirstChildOfClass("Humanoid")
    if not humanoid then
        return
    end
    watched = humanoid
    publish(humanoid)
    connections[#connections + 1] = humanoid.HealthChanged:Connect(function()
        publish(humanoid)
    end)
    connections[#connections + 1] = humanoid.StateChanged:Connect(function(_, new)
        if new == FREEFALL then
            peak = rootHeight(humanoid)
        elseif new == FLYING then
            peak = nil
        elseif peak and (new == LANDED or new == RUNNING) then
            landed(humanoid)
        end
    end)
    connections[#connections + 1] = humanoid.Died:Connect(function()
        deaths = deaths + 1
        workspace:SetAttribute("BlockWorldDeaths", deaths)
        notify("You died. Back at the spawn in " .. RESPAWN_SECONDS .. " seconds.")
    end)
end

local function watch(player)
    player.CharacterAdded:Connect(follow)
    if player.Character then
        follow(player.Character)
    end
end

pcall(function()
    Players.RespawnTime = RESPAWN_SECONDS
end)

for _, player in ipairs(Players:GetPlayers()) do
    watch(player)
end
Players.PlayerAdded:Connect(watch)

-- While a fall lasts, the peak follows the root up (a jump) and stays at the highest point.
RunService.Heartbeat:Connect(function()
    local humanoid = watched
    if not peak or not humanoid or humanoid.Health <= 0 then
        return
    end
    local height = rootHeight(humanoid)
    if height and height > peak then
        peak = height
    end
end)

workspace:GetAttributeChangedSignal("BlockWorldCreative"):Connect(function()
    if creative() and watched and watched.Health > 0 then
        watched.Health = watched.MaxHealth
    end
end)

print("[blockworld] survival ready - press G for survival: falls hurt, at zero health you respawn at the spawn.")
