--[[@coreai
id: sample_waypoint_platform
name: Waypoint Platform
version: 1.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: An anchored platform follows four explicit waypoints at a steady speed, with bounded catch-up after a long frame. This is scripted movement, not automatic pathfinding.
]]
local root = Instance.new("Folder")
root.Name = "WaypointPlatform"
root.Parent = workspace
local points = {Vector3.new(-4, 1, -3), Vector3.new(4, 1, -3), Vector3.new(4, 4, 3), Vector3.new(-4, 4, 3)}
local platform = Instance.new("Part")
platform.Name = "Platform"
platform.Anchored = true
platform.Size = Vector3.new(3, 0.5, 3)
platform.Position = points[1]
platform.Color = Color3.fromRGB(45, 175, 230)
platform.Parent = root
for i, point in ipairs(points) do
    local marker = Instance.new("Part")
    marker.Name = "Waypoint" .. i
    marker.Anchored = true
    marker.CanCollide = false
    marker.Size = Vector3.new(0.3, 0.3, 0.3)
    marker.Position = point + Vector3.new(0, 0.5, 0)
    marker.Material = Enum.Material.Neon
    marker.Parent = root
end
local target = 2
local arrivals = 0
root:SetAttribute("Arrivals", arrivals)
game:GetService("RunService").Heartbeat:Connect(function(dt)
    local offset = points[target] - platform.Position
    local distance = offset.Magnitude
    local step = 3 * math.min(math.max(dt, 0), 0.1)
    if distance <= step then
        platform.Position = points[target]
        target = target % #points + 1
        arrivals = arrivals + 1
        root:SetAttribute("Arrivals", arrivals)
    elseif distance > 0 then
        platform.Position = platform.Position + offset.Unit * step
    end
    root:SetAttribute("TargetWaypoint", target)
end)
