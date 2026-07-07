--[[@coreai
id: coroutine_countdown
name: Coroutine Countdown
version: 1.0.0
active: false
capabilities: All
author: CoreAI
description: A Lua coroutine that yields across ticks - one step per second. Exercises coroutine.yield (WebGL-safe under Lua-CSharp).
]]

-- coroutine_countdown.lua - demonstrates a Lua coroutine driven across frames.
--
-- A coroutine lets a mod spread a sequence over time WITHOUT blocking: it yields,
-- the host advances the frame, and the mod resumes it on the next tick. Under the
-- Lua-CSharp runtime this is frame-pumped, so coroutine.yield works on WebGL too
-- (a blocking wait would deadlock single-threaded WASM - the pump avoids that).

-- Build a coroutine that counts down, yielding after each number.
local function make_countdown(from)
    return coroutine.create(function()
        for i = from, 1, -1 do
            report("[countdown] " .. i)
            coroutine.yield()          -- pause here; resumed on the next tick
        end
        report("[countdown] liftoff!")
    end)
end

local co = make_countdown(5)

-- Resume one step per second. When it finishes, start a fresh countdown so the demo loops.
hooks_every(1.0, function()
    if coroutine.status(co) == "dead" then
        co = make_countdown(5)
    end
    local ok, err = coroutine.resume(co)
    if not ok then
        report("[countdown] error: " .. tostring(err))
    end
end)

report("[coroutine_countdown] loaded - counts down one step per second")
