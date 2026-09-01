local function read_count(key)
    local value = tonumber(store_get(key))
    if value == nil then
        return 0
    end
    return value
end

local function burn_guarded_budget()
    local total = 0
    for index = 1, 280 do
        total = total + index
    end
    return total
end

local deferred_count = read_count("deferred_count")
local delayed_count = read_count("delayed_count")
local timer_count = read_count("timer_count")
local event_count = read_count("event_count")

hooks_every(0.5, function()
    burn_guarded_budget()
    timer_count = timer_count + 1
    store_set("timer_count", tostring(timer_count))
end)

hooks_on("__G10_EVENT_NAME__", function(_, payload)
    burn_guarded_budget()
    event_count = event_count + 1
    store_set("event_count", tostring(event_count))
    store_set("event_probe", payload)
end)

for index = 1, 10 do
    task.defer(function()
        burn_guarded_budget()
        deferred_count = deferred_count + 1
        store_set("deferred_count", tostring(deferred_count))
    end)
end

local delayed_deadlines = { __G10_DELAY_SECONDS__ }
for index = 1, #delayed_deadlines do
    task.delay(delayed_deadlines[index], function()
        burn_guarded_budget()
        delayed_count = delayed_count + 1
        store_set("delayed_count", tostring(delayed_count))
    end)
end
