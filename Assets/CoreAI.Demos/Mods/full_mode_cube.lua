-- full_mode_cube.lua - a FULL-mode mod that moves a scene object via reflection.
--
-- The unity_* functions are gated behind LuaCapabilities.Full. Full is opt-in and is NOT
-- part of LuaCapabilities.All: enable "Enable Full Lua Access" on the CoreAILifetimeScope,
-- or load this mod with (LuaCapabilities.All | LuaCapabilities.Full). Without Full, the
-- unity_* functions are simply absent from the sandbox globals and calling them errors.

-- React to the host event "tweak_cube" and nudge the target cube straight up.
hooks_on("tweak_cube", function(name, payload)
    -- unity_find(name) returns the GameObject instance id, or 0 when nothing matches.
    local id = unity_find("TargetCube")
    if id == 0 then
        report("[full_mode_cube] TargetCube not found in the scene")
        return
    end

    -- unity_get_position(id) returns a table { x, y, z } of the world position.
    local pos = unity_get_position(id)

    -- unity_set_position(id, x, y, z) writes the world position back. Move up by 1 unit.
    unity_set_position(id, pos.x, pos.y + 1.0, pos.z)

    report("[full_mode_cube] raised TargetCube to y=" .. string.format("%.2f", pos.y + 1.0))
end)

report("[full_mode_cube] loaded - emit 'tweak_cube' to move the cube (needs Full Lua access)")
