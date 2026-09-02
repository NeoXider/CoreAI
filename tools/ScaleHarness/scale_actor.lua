-- Frozen per-actor workload for the scale staircase (tools/ScaleHarness/scale.workload.json).
-- One client mod per actor: bounded work per Heartbeat, a persistent task.wait loop, a RemoteEvent
-- round trip to the host server mod, and bounded object churn through the ACL/quota-checked registry.
local RunService = game:GetService('RunService')
local Players = game:GetService('Players')
local localPlayer = Players.LocalPlayer
assert(localPlayer ~= nil, 'client mod must observe its LocalPlayer')
local remote = workspace:FindFirstChild('ScaleRemote')
assert(remote ~= nil, 'the host server mod must publish workspace.ScaleRemote before clients load')

local ACTOR_INDEX = __SCALE_ACTOR_INDEX__
local WORK = __SCALE_WORK__
local REMOTE_EVERY = __SCALE_REMOTE_EVERY__
local SPAWN_EVERY = __SCALE_SPAWN_EVERY__
local WAIT_SECONDS = __SCALE_WAIT_SECONDS__

local heartbeats = 0
local sent = 0
local acks = 0
local spawned = 0
local loops = 0
local checksum = 0
local part = nil

remote.OnClientEvent:Connect(function(seq)
    acks = acks + 1
end)

RunService.Heartbeat:Connect(function(dt)
    heartbeats = heartbeats + 1
    local total = 0
    for index = 1, WORK do
        total = total + index
    end
    checksum = (checksum + total) % 1000000007
    if (heartbeats + ACTOR_INDEX) % REMOTE_EVERY == 0 then
        sent = sent + 1
        remote:FireServer(sent)
    end
    if (heartbeats + ACTOR_INDEX) % SPAWN_EVERY == 0 then
        if part ~= nil then
            part:Destroy()
        end
        part = Instance.new('Part')
        part.Name = 'ScalePart'
        part.Parent = workspace
        spawned = spawned + 1
    end
end)

task.spawn(function()
    while true do
        task.wait(WAIT_SECONDS)
        loops = loops + 1
    end
end)

hooks_on('__SCALE_SNAPSHOT_EVENT__', function()
    store_set('heartbeats', tostring(heartbeats))
    store_set('sent', tostring(sent))
    store_set('acks', tostring(acks))
    store_set('spawned', tostring(spawned))
    store_set('loops', tostring(loops))
    store_set('checksum', tostring(checksum))
end)
