--[[@coreai
id: sample_recycling_conveyor
name: Recycling Conveyor
version: 1.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Twelve reusable crates move along a conveyor. Only four crates are updated per frame; absolute-time positions avoid losing motion between updates.
]]
local root = Instance.new("Folder")
root.Name = "RecyclingConveyor"
root.Parent = workspace
local belt = Instance.new("Part")
belt.Name = "Belt"
belt.Anchored = true
belt.Size = Vector3.new(18, 0.4, 3)
belt.Position = Vector3.new(0, 0.5, 0)
belt.Color = Color3.fromRGB(35, 50, 70)
belt.Parent = root
local crates = {}
for i = 1, 12 do
    local crate = Instance.new("Part")
    crate.Name = "Crate" .. i
    crate.Anchored = true
    crate.Size = Vector3.new(0.8, 0.8, 0.8)
    crate.Color = Color3.fromRGB(45 + i * 12, 160, 220)
    crate.Position = Vector3.new(-8 + (i - 1) * 1.4, 1.2, 0)
    crate.Parent = root
    crates[i] = crate
end
local elapsed = 0
local cursor = 1
local frames = 0
root:SetAttribute("CrateCount", #crates)
root:SetAttribute("UpdatesPerFrame", 4)
game:GetService("RunService").Heartbeat:Connect(function(dt)
    elapsed = elapsed + math.min(dt, 0.1)
    for _ = 1, 4 do
        local x = (elapsed * 3 + (cursor - 1) * 1.4) % 16 - 8
        crates[cursor].Position = Vector3.new(x, 1.2, 0)
        cursor = cursor % #crates + 1
    end
    frames = frames + 1
    root:SetAttribute("Frames", frames)
end)
