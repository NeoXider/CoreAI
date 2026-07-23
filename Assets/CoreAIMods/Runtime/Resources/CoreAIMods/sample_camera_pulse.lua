--[[@coreai
id: sample_camera_pulse
name: Camera Colour Pulse (sample)
version: 1.0.1
active: false
capabilities: All, Full
category: Samples
author: CoreAI
description: Opt-in bundled sample. When enabled, gently pulses the main camera background colour every couple of seconds. Ships disabled — enable it from the Hub Mods tab. NEEDS the Full tier - if the host does not grant Full Lua access (CoreAiModsInstaller enableFullLuaAccess), the mod stays idle and reports why instead of erroring.
]]

-- A richer bundled sample that touches the scene via the Full tier (unity_* reflection). It ships
-- DISABLED (active: false) so it never changes a game's look until the player opts in from the Mods
-- tab. Full-tier reflection is host/singleplayer-only and is stripped for networked clients.
--
-- The header asks for Full, but the host ceiling masks capability grants: under the default
-- composition Full is withheld and every unity_* call raises the withheld-capability error.
-- The tick below wraps the work in pcall and downgrades to a single explanatory report instead of
-- erroring every tick into quarantine.

local function random_channel()
    -- WHY: math.random() with no args returns a float in [0,1) for a smooth channel; math.random(0,1)
    -- would return only the integers 0 or 1, collapsing the palette to 8 hard corner colours.
    return math.random()
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

local full_tier_unavailable = false

hooks_every(2.0, function()
    if full_tier_unavailable then
        return
    end
    local ok, painted = pcall(apply_colour)
    if not ok then
        full_tier_unavailable = true
        report("[camera_pulse] idle: this sample needs the Full capability, which this host does not grant. " ..
            "Enable Full Lua access (host opt-in) to see it pulse. Details: " .. tostring(painted))
        return
    end
    if painted then
        report("[camera_pulse] repainted the camera background")
    end
end)

report("[camera_pulse] loaded (enable me to start pulsing the camera colour; needs the Full tier)")
