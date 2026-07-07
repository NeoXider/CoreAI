--[[@coreai
id: day_night_cycle
name: Day/Night Cycle
version: 1.0.0
active: false
capabilities: All
author: CoreAI
description: A live game MECHANIC - advances a day/night phase on a timer and emits phase_changed events for the game to react to. No tier needed.
]]

-- day_night_cycle.lua - a "live mechanic" the AI can add while playing.
--
-- Design rule (native/Lua boundary): the mod DECLARES a mechanic and reacts to time;
-- it does NOT run a per-frame hot loop. It advances a phase on a timer and EMITS an
-- event; the game (C#) applies the actual lighting/spawn changes when it receives the
-- event. Routing changes through events/commands (not direct mutation) keeps this
-- deterministic and ready for host-authoritative multiplayer.

local PHASES = { "dawn", "day", "dusk", "night" }
local SECONDS_PER_PHASE = 15.0

-- Resume from the persisted phase so a reload does not restart the cycle.
local index = tonumber(store_get("phase_index")) or 1

local function announce(phase)
    events_emit("phase_changed", phase)      -- the game reacts (lighting, ambience, spawns)
    report("[day_night] phase -> " .. phase)
end

announce(PHASES[index])                      -- emit the current phase on load

hooks_every(SECONDS_PER_PHASE, function()
    index = (index % #PHASES) + 1
    store_set("phase_index", tostring(index))
    announce(PHASES[index])
end)

-- Let the game or another mod jump straight to a phase by name.
hooks_on("set_phase", function(_, payload)
    for i, p in ipairs(PHASES) do
        if p == payload then
            index = i
            store_set("phase_index", tostring(index))
            announce(p)
            return
        end
    end
    report("[day_night] unknown phase '" .. tostring(payload) .. "'")
end)

report("[day_night_cycle] loaded - cycles dawn/day/dusk/night, emits 'phase_changed'")
