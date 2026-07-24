--[[@coreai
id: sample_lane_racer
name: Lane Racer (sample)
version: 2.1.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Opt-in playable sample. A 3-lane dodging mini-game - steer with A/D (or Left/Right) to weave past oncoming blocks; press R or Space to restart after a crash. Written in pure Roblox API (Instance.new, Vector3, Color3, UserInputService, CFrame, RunService.Heartbeat, print) so it imports/exports 1:1. Ships disabled; enable it from the Hub Mods tab. Its parts are removed when the mod is disabled or unloaded.
]]

-- A complete playable mod in the SAME API Roblox uses. The game loop is RunService.Heartbeat (per
-- frame, dt in seconds) so motion is smooth and frame-rate independent; speeds are in studs/second.
-- Input is UserInputService with rising-edge detection (held key = one lane per press). The camera is
-- a scripted CFrame.lookAt framed so world +X is SCREEN RIGHT, so A=left / D=right feel correct.
-- Parts are Anchored (no physics) during play and unanchored on a crash so the wreck scatters. Every
-- part lives under one Folder the mod owns, so disable/unload destroys the whole game.

local RunService = game:GetService("RunService")
local uis = game:GetService("UserInputService")

local LANE_X = { -4, 0, 4 }          -- world X of lanes 1..3 (lane 3 = +X = screen right)
local SPAWN_Z = -60                  -- blocks appear this far down the track (far end of the view)
local DESPAWN_Z = 8                  -- and are recycled once this far past the car
local SPEED = 20                     -- studs PER SECOND a block travels toward the car
local SPAWN_INTERVAL = 0.8           -- seconds between spawns
local MAX_OBSTACLES = 6

local root = Instance.new("Folder")
root.Name = "LaneRacer"
root.Parent = workspace

local car = Instance.new("Part")
car.Name = "RacerCar"
car.Size = Vector3.new(2, 1, 3)
car.Color = Color3.fromRGB(60, 140, 255)
car.Anchored = true
car.Parent = root

local lane = 2
local carX = LANE_X[lane]                 -- visual X, eased toward the target lane for a smooth slide
local LANE_EASE = 12                       -- higher = snappier lane changes
car.Position = Vector3.new(carX, 1, 0)

local cam = workspace.CurrentCamera
cam.CameraType = Enum.CameraType.Scriptable
cam.CFrame = CFrame.lookAt(Vector3.new(0, 9, 14), Vector3.new(0, 2, -18))

local obstacles = {}                 -- { {part=, lane=, z=} , ... }
local prevLeft, prevRight, prevRestart = false, false, false
local spawnTimer = 0
local score = 0
local alive = true

local function clear_obstacles()
    for i = #obstacles, 1, -1 do
        obstacles[i].part:Destroy()
        table.remove(obstacles, i)
    end
end

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
    p.Anchored = true
    p.Parent = root
    obstacles[#obstacles + 1] = { part = p, lane = l, z = SPAWN_Z }
end

local function crash()
    alive = false
    -- Scatter the wreck: unanchor the car and every block so physics tumbles them.
    car.Anchored = false
    for _, o in ipairs(obstacles) do
        o.part.Anchored = false
    end
    print("[lane_racer] CRASH in lane " .. lane .. " - final score " .. score ..
        ". Press R or Space to restart.")
end

local function restart()
    clear_obstacles()
    lane, score, spawnTimer, alive = 2, 0, 0, true
    carX = LANE_X[lane]
    car.Anchored = true
    car.Position = Vector3.new(carX, 1, 0)
    print("[lane_racer] restarted - go!")
end

print("[lane_racer] loaded - A/D (or Left/Right) to switch lanes, dodge the red blocks. R/Space to restart.")

RunService.Heartbeat:Connect(function(dt)
    if not alive then
        local pressed = uis:IsKeyDown(Enum.KeyCode.R) or uis:IsKeyDown(Enum.KeyCode.Space)
        if pressed and not prevRestart then
            restart()
        end
        prevRestart = pressed
        return
    end

    -- Input: rising edge only, so holding the key moves exactly one lane per press.
    local left = uis:IsKeyDown(Enum.KeyCode.A) or uis:IsKeyDown(Enum.KeyCode.Left)
    local right = uis:IsKeyDown(Enum.KeyCode.D) or uis:IsKeyDown(Enum.KeyCode.Right)
    if left and not prevLeft then lane = math.max(1, lane - 1) end
    if right and not prevRight then lane = math.min(3, lane + 1) end
    prevLeft, prevRight = left, right
    -- Ease the visual X toward the target lane so the car slides instead of teleporting.
    carX = carX + (LANE_X[lane] - carX) * math.min(1, dt * LANE_EASE)
    car.Position = Vector3.new(carX, 1, 0)

    -- Advance blocks toward the car by dt (smooth); recycle passed ones, detect a same-lane hit.
    for i = #obstacles, 1, -1 do
        local o = obstacles[i]
        o.z = o.z + SPEED * dt
        o.part.Position = Vector3.new(LANE_X[o.lane], 1, o.z)

        if o.z >= DESPAWN_Z then
            o.part:Destroy()
            table.remove(obstacles, i)
            score = score + 1
            if score % 5 == 0 then
                print("[lane_racer] score " .. score)
            end
        elseif o.lane == lane and o.z <= 1.5 and o.z >= -1.5 then
            crash()
            break
        end
    end

    spawnTimer = spawnTimer + dt
    if spawnTimer >= SPAWN_INTERVAL then
        spawnTimer = spawnTimer - SPAWN_INTERVAL
        spawn_obstacle()
    end
end)
