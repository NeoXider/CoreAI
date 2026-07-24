--[[@coreai
id: sample_welcome
name: Welcome Sample
version: 2.0.0
active: true
capabilities: All
category: Samples
author: CoreAI
description: Minimal bundled mod shipped with the game. Prints a start line on load, then counts five ticks (one every two seconds) before pausing - so a fresh install already has a working, editable, pure-Roblox mod to learn from.
]]

-- A bundled mod written in the SAME API Roblox uses, so it exports/imports 1:1: game:GetService,
-- RunService.Heartbeat (the per-frame loop), print, and plain local state. No CoreAI-only globals.
-- Edit it freely from the Hub -> Mods tab.

local RunService = game:GetService("RunService")

local MAX_TICKS = 5
local ticks = 0
local elapsed = 0

print("[welcome] started - bundled CoreAI mods are working. I will count " .. MAX_TICKS ..
    " ticks, then pause. Open the Hub -> Mods tab to edit or add mods.")

-- Heartbeat runs once per frame with the frame delta (dt) in seconds, exactly like Roblox. Accumulate
-- dt to fire on a wall-clock schedule instead of every frame.
RunService.Heartbeat:Connect(function(dt)
    if ticks >= MAX_TICKS then
        return
    end

    elapsed = elapsed + dt
    if elapsed < 2 then
        return
    end
    elapsed = elapsed - 2

    ticks = ticks + 1
    print("[welcome] tick #" .. ticks .. " / " .. MAX_TICKS)
    if ticks >= MAX_TICKS then
        print("[welcome] reached " .. MAX_TICKS .. " ticks - pausing. Reload me to run again.")
    end
end)
