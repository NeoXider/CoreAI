local seen = {}

local function completeWhenReady()
    if seen.spawn and seen.defer and seen.delay and seen.wait then
        workspace:SetAttribute("TierACorpusResult", "TAC-007-task-scheduling")
    end
end

task.spawn(function()
    seen.spawn = true
    local elapsed = task.wait(0.25)
    assert(elapsed >= 0.25)
    seen.wait = true
    completeWhenReady()
end)

task.defer(function()
    seen.defer = true
    completeWhenReady()
end)

task.delay(0.25, function()
    seen.delay = true
    completeWhenReady()
end)
