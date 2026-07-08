--[[@coreai
id: sample_welcome
name: Welcome Sample
version: 1.0.0
active: true
capabilities: All
category: Samples
author: CoreAI
description: Minimal bundled mod shipped with the game. Greets on load and ticks a persistent counter, so a fresh install already has a working, editable mod to learn from.
]]

-- A bundled mod: this file lives in Resources/CoreAIMods and is seeded into the mod store on first
-- run by the BundledModSeeder. It uses only tier-independent APIs (report/hooks_*/store_*), so it runs
-- under any capability grant. Edit it freely — your edits are preserved across game updates.

report("[welcome] Bundled CoreAI mods are working. Open the Hub → Mods tab to edit or add mods.")

hooks_every(30.0, function()
    local ticks = (tonumber(store_get("ticks")) or 0) + 1
    store_set("ticks", tostring(ticks))
    report("[welcome] still running — tick #" .. ticks)
end)
