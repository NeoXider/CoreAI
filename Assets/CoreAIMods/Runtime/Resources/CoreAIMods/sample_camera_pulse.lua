--[[@coreai
id: sample_camera_pulse
name: Camera Colour Pulse (sample)
version: 1.0.0
active: false
capabilities: All, Full
category: Samples
author: CoreAI
description: Opt-in bundled sample. When enabled, gently pulses the main camera background colour every couple of seconds. Ships disabled — enable it from the Hub Mods tab to see a Full-tier mod in action.
]]

-- A richer bundled sample that touches the scene via the Full tier (unity_* reflection). It ships
-- DISABLED (active: false) so it never changes a game's look until the player opts in from the Mods
-- tab. Full-tier reflection is host/singleplayer-only and is stripped for networked clients.

local function random_channel()
    return math.random(0, 1)
end

local function apply_colour()
    local cameras = unity_find_all("Camera", 8)
    for _, cam_id in ipairs(cameras) do
        local components = unity_list_components(cam_id)
        for _, comp_name in ipairs(components) do
            if comp_name == "Camera" then
                local hex = string.format("#%02x%02x%02x",
                    math.floor(random_channel() * 255),
                    math.floor(random_channel() * 255),
                    math.floor(random_channel() * 255))
                unity_set_member(cam_id, "Camera", "BackgroundColor", hex)
                return true
            end
        end
    end
    return false
end

hooks_every(2.0, function()
    if apply_colour() then
        report("[camera_pulse] repainted the camera background")
    end
end)

report("[camera_pulse] loaded (enable me to start pulsing the camera colour)")
