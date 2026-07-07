--[[@coreai
id: stats_provider
name: Shared Stats Provider
version: 1.0.0
active: false
capabilities: All
author: CoreAI
description: Publishes a value and a callable function for OTHER mods via mods_export. The provider side of the inter-mod API.
]]

-- shared_stats_provider.lua - the PROVIDER side of cross-mod communication.
--
-- mods_export(name, value) publishes something under this mod's id ("stats_provider").
-- Another mod reads plain data with mods_get(id, name) and calls functions with
-- mods_call(id, name, ...). Only PLAIN DATA crosses the boundary - functions are
-- invoked on this mod's own state and only their (copied) return value comes back.
-- No closures, live tables, or references ever leak between mods (multiplayer-safe).

local base_multiplier = 2.0

-- 1. Export a plain value. Consumers read it with mods_get("stats_provider", "multiplier").
mods_export("multiplier", base_multiplier)

-- 2. Export a function. Consumers CALL it with mods_call("stats_provider", "scale", 10).
--    (mods_get on a function returns nil - use mods_call.)
mods_export("scale", function(x)
    return (tonumber(x) or 0) * base_multiplier
end)

-- 3. Let others reconfigure the multiplier at runtime via an event.
hooks_on("set_multiplier", function(_, payload)
    base_multiplier = tonumber(payload) or base_multiplier
    mods_export("multiplier", base_multiplier)   -- re-publish the new value
    report("[stats_provider] multiplier set to " .. base_multiplier)
end)

report("[stats_provider] loaded - exports 'multiplier' (value) and 'scale(x)' (function)")
