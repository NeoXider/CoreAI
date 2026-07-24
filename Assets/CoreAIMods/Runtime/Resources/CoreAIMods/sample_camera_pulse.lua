--[[@coreai
id: sample_camera_pulse
name: Colour Pulse (sample)
version: 2.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Opt-in bundled sample. When enabled it spawns a beacon block and smoothly cycles its colour every couple of seconds using only the standard Roblox-style API (Instance.new + Color3.fromHSV). Ships disabled - enable it from the Hub Mods tab. Disabling or deleting the mod removes the beacon automatically.
]]

-- A bundled sample that stays entirely within the STANDARD tier: it spawns one Part and pulses its
-- Color3 on a timer. No unity_* reflection and no Full grant, so it works under the default capability
-- grant (the previous version called unity_find_all, which the host withholds - that error unwinds past
-- pcall and quarantined the mod). It ships DISABLED (active: false) so it never changes a fresh game
-- until the player opts in; the beacon it owns is destroyed automatically when the mod is disabled or
-- unloaded (the runtime sweeps instances a mod created).

local beacon = Instance.new("Part")
beacon.Name = "PulseBeacon"
beacon.Size = Vector3.new(2, 2, 2)
beacon.Position = Vector3.new(0, 6, 0)
beacon.Parent = workspace

local hue = 0.0

hooks_every(2.0, function()
    -- WHY: cycle hue in HSV for an even, saturated sweep across the whole colour wheel; stepping the
    -- RGB channels independently would clump around the eight hard corner colours instead.
    hue = (hue + 0.13) % 1.0
    beacon.Color = Color3.fromHSV(hue, 0.85, 1.0)
    report("[colour_pulse] beacon hue -> " .. string.format("%.2f", hue))
end)

report("[colour_pulse] loaded - a colour-cycling beacon is now pulsing in the world.")
