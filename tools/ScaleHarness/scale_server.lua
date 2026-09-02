-- Frozen host-side server mod for the scale staircase: owns the shared RemoteEvent and answers every
-- client FireServer with a targeted FireClient so each actor observes a full loopback round trip.
local remote = Instance.new('RemoteEvent')
remote.Name = 'ScaleRemote'
remote.Parent = workspace
local received = 0

remote.OnServerEvent:Connect(function(player, seq)
    received = received + 1
    remote:FireClient(player, seq)
end)

hooks_on('__SCALE_SNAPSHOT_EVENT__', function()
    store_set('received', tostring(received))
end)
