--[[@coreai
id: first_person
name: First-Person Controller
version: 1.0.0
active: false
capabilities: All, Full
author: CoreAI
description: WASD/arrows to walk, mouse to look. Drives a named scene object (default "Player").
]]

-- first_person_controller.lua - a FULL-mode mod that drives a scene object like a
-- first-person character: WASD/arrows to walk, mouse to look around.
--
-- Requires LuaCapabilities.Full (the unity_* reflection APIs) AND the Gameplay tier
-- (input_* / time_*). Full is opt-in and NOT part of LuaCapabilities.All: enable
-- "Enable Full Lua Access" on the CoreAILifetimeScope, or load with
-- (LuaCapabilities.All | LuaCapabilities.Full).
--
-- Point it at any scene object by name. Move the Main Camera for a classic FPS view,
-- or a body object that the camera is parented to. Default target: "Player".

-- ---- config -----------------------------------------------------------------
local TARGET_NAME  = "Player"   -- scene object to drive (e.g. "Main Camera", "Player")
local MOVE_SPEED   = 4.0        -- metres / second
local LOOK_SPEED   = 3.0        -- degrees per unit of mouse delta
local PITCH_MIN    = -85.0      -- look-down clamp
local PITCH_MAX    = 85.0       -- look-up clamp
-- -----------------------------------------------------------------------------

local yaw, pitch = 0.0, 0.0     -- accumulated look angles
local target_id  = 0            -- cached GameObject id (0 = not resolved)
local last_t     = time_now()   -- for real elapsed time between handler calls
local seeded     = false        -- whether we've adopted the object's start rotation

local function resolve()
    if target_id == 0 then
        target_id = unity_find(TARGET_NAME)
        if target_id ~= 0 and not seeded then
            local t = unity_get_transform(target_id)
            if t and t.rotation then
                yaw, pitch = t.rotation.y, t.rotation.x
                seeded = true
            end
        end
    end
    return target_id ~= 0
end

hooks_on("tick", function()
    if not resolve() then
        return
    end

    -- Real elapsed time since the last tick (the tick handler runs at ~20 Hz, so
    -- time_delta() -- the *frame* delta -- would undercount; measure it ourselves).
    local now = time_now()
    local dt = now - last_t
    last_t = now
    if dt <= 0.0 then
        return
    end

    -- ---- look (mouse) -------------------------------------------------------
    yaw = yaw + input_axis("Mouse X") * LOOK_SPEED
    pitch = pitch - input_axis("Mouse Y") * LOOK_SPEED
    if pitch < PITCH_MIN then pitch = PITCH_MIN end
    if pitch > PITCH_MAX then pitch = PITCH_MAX end

    -- ---- move (WASD / arrows / gamepad axes) --------------------------------
    local h = input_axis("Horizontal")  -- A/D, left/right
    local v = input_axis("Vertical")    -- W/S, up/down

    -- Yaw-relative basis on the XZ plane (Unity: yaw 0 => forward = +Z).
    local rad = math.rad(yaw)
    local sin, cos = math.sin(rad), math.cos(rad)
    local fx, fz = sin, cos       -- forward
    local rx, rz = cos, -sin      -- right

    local step = MOVE_SPEED * dt
    local dx = (fx * v + rx * h) * step
    local dz = (fz * v + rz * h) * step

    if dx ~= 0.0 or dz ~= 0.0 then
        local p = unity_get_position(target_id)
        if p then
            unity_set_position(target_id, p.x + dx, p.y, p.z + dz)
        end
    end

    -- ---- apply look ---------------------------------------------------------
    unity_set_rotation_euler(target_id, pitch, yaw, 0.0)
end)

report("[first_person] loaded - driving '" .. TARGET_NAME ..
    "' (WASD to move, mouse to look; needs Full Lua access)")
