--[[@coreai
id: sample_welcome
name: Welcome Sample
version: 1.1.0
active: true
capabilities: All
category: Samples
author: CoreAI
description: Minimal bundled mod shipped with the game. Logs a start line on load and then ticks a counter five times (once every two seconds) before pausing, so a fresh install already has a working, editable mod to learn from.
]]

-- A bundled mod: this file lives in Resources/CoreAIMods and is seeded into the mod store on first
-- run by the BundledModSeeder. It uses only tier-independent APIs (report/hooks_*/store_*), so it runs
-- under any capability grant. Edit it freely — your edits are preserved across game updates.

local MAX_TICKS = 5

-- WHY: reset the counter on every (re)load so each enable produces a clean run of five ticks —
-- handy when learning the enable/disable lifecycle from the Hub Mods tab.
store_set("ticks", "0")
report("[welcome] started — bundled CoreAI mods are working. I will tick " .. MAX_TICKS ..
    " times, then pause. Open the Hub -> Mods tab to edit or add mods.")

hooks_every(2.0, function()
    local ticks = tonumber(store_get("ticks")) or 0
    if ticks >= MAX_TICKS then
        return
    end

    ticks = ticks + 1
    store_set("ticks", tostring(ticks))
    report("[welcome] tick #" .. ticks .. " / " .. MAX_TICKS)

    if ticks >= MAX_TICKS then
        report("[welcome] reached " .. MAX_TICKS .. " ticks — pausing. Disable/enable me to run again.")
    end
end)
