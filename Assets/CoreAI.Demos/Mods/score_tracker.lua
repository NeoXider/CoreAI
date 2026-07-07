--[[@coreai
id: score_tracker
name: Score Tracker
version: 1.0.0
active: false
capabilities: All
author: CoreAI
description: Listens for "score" events, keeps a running total in the per-mod store, and reports it. No tier needed.
]]

-- score_tracker.lua - shows the always-available mod APIs (no capability tier required):
--   hooks_on / events_emit / store_set / store_get / report.
-- Emit  events_emit("score", "10")  from the game or another mod to add 10 points.

-- Add a "reset" handler so the running total can be cleared at runtime.
hooks_on("score_reset", function()
    store_set("total", "0")
    report("[score_tracker] total reset to 0")
end)

-- Each "score" event carries a numeric payload (as a string). Accumulate it.
hooks_on("score", function(_, payload)
    local total = (tonumber(store_get("total")) or 0) + (tonumber(payload) or 0)
    store_set("total", tostring(total))          -- persists across frames AND reloads
    report("[score_tracker] +" .. tostring(payload) .. " -> total " .. total)
end)

-- Periodically announce the total so you can see it without emitting events.
hooks_every(10.0, function()
    report("[score_tracker] current total: " .. (store_get("total") or "0"))
end)

report("[score_tracker] loaded - emit 'score' with a number payload to add points")
