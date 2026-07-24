--[[@coreai
id: sample_lane_racer
name: Lane Racer (sample)
version: 1.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Opt-in playable sample. A 3-lane dodging mini-game - steer the car between three lanes with A/D (or Left/Right) to weave past oncoming blocks. Uses only the standard Roblox-style API (Instance.new, Vector3, Color3, UserInputService) plus a game-loop timer. Ships disabled; enable it from the Hub Mods tab. All spawned parts are removed when the mod is disabled or unloaded.
]]

-- A small but complete playable mod, kept entirely within the STANDARD tier. It demonstrates the
-- three pillars a game mod needs: spawning (Instance.new -> workspace), input (UserInputService
-- IsKeyDown with rising-edge detection so a held key steps one lane, not many), and a fixed game loop
-- (hooks_every). State lives in upvalues that persist across ticks. The camera follows the car via
-- workspace.CurrentCamera.CameraSubject. Every Part is parented under one Folder the mod owns, so the
-- runtime destroys the whole game when the mod is disabled/unloaded - no manual teardown needed.

local uis = game:GetService("UserInputService")

local LANE_X = { -4, 0, 4 }          -- world X of lanes 1..3
local SPAWN_Z = 60                   -- obstacles appear this far ahead
local DESPAWN_Z = -8                 -- and are recycled once this far behind
local SPEED = 1.2                    -- studs per tick an obstacle moves toward the car
local SPAWN_EVERY = 14               -- ticks between spawns
local MAX_OBSTACLES = 6

local root = Instance.new("Folder")
root.Name = "LaneRacer"
root.Parent = workspace

local car = Instance.new("Part")
car.Name = "RacerCar"
car.Size = Vector3.new(2, 1, 3)
car.Color = Color3.fromRGB(60, 140, 255)
car.Parent = root

local lane = 2
car.Position = Vector3.new(LANE_X[lane], 1, 0)
workspace.CurrentCamera.CameraSubject = car

local obstacles = {}                 -- { {part=, lane=, z=} , ... }
local prevLeft, prevRight = false, false
local tick = 0
local score = 0
local alive = true

local function spawn_obstacle()
    if #obstacles >= MAX_OBSTACLES then
        return
    end
    local l = math.random(1, 3)
    local p = Instance.new("Part")
    p.Name = "Block"
    p.Size = Vector3.new(2, 2, 2)
    p.Color = Color3.fromRGB(230, 70, 70)
    p.Position = Vector3.new(LANE_X[l], 1, SPAWN_Z)
    p.Parent = root
    obstacles[#obstacles + 1] = { part = p, lane = l, z = SPAWN_Z }
end

report("[lane_racer] loaded - press A/D (or Left/Right) to switch lanes and dodge the red blocks.")

hooks_every(0.06, function()
    if not alive then
        return
    end
    tick = tick + 1

    -- Input: rising edge only, so holding the key moves exactly one lane per press.
    local left = uis:IsKeyDown(Enum.KeyCode.A) or uis:IsKeyDown(Enum.KeyCode.Left)
    local right = uis:IsKeyDown(Enum.KeyCode.D) or uis:IsKeyDown(Enum.KeyCode.Right)
    if left and not prevLeft then lane = math.max(1, lane - 1) end
    if right and not prevRight then lane = math.min(3, lane + 1) end
    prevLeft, prevRight = left, right
    car.Position = Vector3.new(LANE_X[lane], 1, 0)

    -- Advance obstacles toward the car; recycle the ones that pass, detect a same-lane hit.
    for i = #obstacles, 1, -1 do
        local o = obstacles[i]
        o.z = o.z - SPEED
        o.part.Position = Vector3.new(LANE_X[o.lane], 1, o.z)

        if o.z <= DESPAWN_Z then
            o.part:Destroy()
            table.remove(obstacles, i)
            score = score + 1
            if score % 5 == 0 then
                report("[lane_racer] score " .. score)
            end
        elseif o.lane == lane and o.z <= 1.5 and o.z >= -1.5 then
            alive = false
            report("[lane_racer] CRASH in lane " .. lane .. " - final score " .. score ..
                ". Disable/enable the mod to race again.")
        end
    end

    if tick % SPAWN_EVERY == 0 then
        spawn_obstacle()
    end
end)
