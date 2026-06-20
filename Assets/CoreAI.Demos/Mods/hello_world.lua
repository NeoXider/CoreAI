-- hello_world.lua - the minimal first CoreAI Lua mod.
--
-- A mod is just Lua source that runs once through LuaModRuntime.LoadMod. During that
-- run it registers hooks; afterwards the runtime drives those hooks every frame. The
-- mod APIs used here (hooks_*, store_*, events_emit, report) are always available to a
-- mod, independent of the capability tier; tiers (Read/WorldEdit/LogicOverride/Full)
-- only gate the GAME bindings (coreai_world_*, logic_*, unity_*). So this mod runs under
-- any grant - the default LuaCapabilities.All is plenty and no Full Lua access is needed.

-- 1. React to a game event.
--    hooks_on(eventName, handler) calls handler(eventName, payload) every time the host
--    (or another mod) emits "ping". The handler receives the event name and a string payload.
hooks_on("ping", function(name, payload)
    -- report(message) sends a line back to the game (LuaModRuntime.ModReportEmitted).
    -- Report output is muted by default; the host enables it per mod for diagnostics.
    report("[hello_world] got '" .. name .. "' with payload '" .. tostring(payload) .. "'")
end)

-- 2. Run code on a timer.
--    hooks_every(seconds, handler) calls handler on a fixed interval (>= 0.05 s).
--    Here we keep a persistent counter and report it once per tick.
hooks_every(5.0, function()
    -- 3. Persist state across frames (and across reloads) with the per-mod key/value store.
    --    store_get/store_set work with string values; convert to/from numbers as needed.
    local ticks = (tonumber(store_get("ticks")) or 0) + 1
    store_set("ticks", tostring(ticks))
    report("[hello_world] tick #" .. ticks)
end)

-- That is the whole mod: one event hook, one timer, and a persistent counter.
report("[hello_world] loaded - emit 'ping' to see the event handler fire")
