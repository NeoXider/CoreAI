--[[@coreai
id: stats_consumer
name: Shared Stats Consumer
version: 1.0.0
active: false
capabilities: All
author: CoreAI
description: Reads a value and calls a function from the "stats_provider" mod via mods_get/mods_call. The consumer side of the inter-mod API.
]]

-- shared_stats_consumer.lua - the CONSUMER side of cross-mod communication.
-- Pair this with shared_stats_provider.lua (id: stats_provider). Load order does not
-- matter for reads at call time - just make sure the provider is loaded before the call.

local PROVIDER = "stats_provider"

hooks_every(5.0, function()
    -- mods_get(targetId, name) -> a copied plain-data value (nil if absent or a function).
    local multiplier = mods_get(PROVIDER, "multiplier")
    if multiplier == nil then
        report("[stats_consumer] provider '" .. PROVIDER .. "' not loaded yet")
        return
    end

    -- mods_call(targetId, name, ...) -> runs the exported function on the provider's
    -- own state and returns a copy of its result.
    local scaled = mods_call(PROVIDER, "scale", 10)

    report("[stats_consumer] multiplier=" .. tostring(multiplier) ..
        "  scale(10)=" .. tostring(scaled))
end)

-- List what the provider exposes (introspection helps an AI discover callable APIs).
hooks_on("list_provider_exports", function()
    local names = mods_list_exports(PROVIDER)
    report("[stats_consumer] " .. PROVIDER .. " exports: " .. table.concat(names or {}, ", "))
end)

report("[stats_consumer] loaded - reads 'multiplier' and calls 'scale' from " .. PROVIDER)
