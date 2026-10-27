--[[@coreai
id: sample_round_clock
name: Round Clock
version: 1.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: A bounded warmup/play/rest state machine. Its beacon changes colour, and attributes expose the phase, round and remaining time.
]]
local root = Instance.new("Folder")
root.Name = "RoundClock"
root.Parent = workspace
local beacon = Instance.new("Part")
beacon.Name = "PhaseBeacon"
beacon.Anchored = true
beacon.Position = Vector3.new(0, 3, 0)
beacon.Size = Vector3.new(2, 4, 2)
beacon.Material = Enum.Material.Neon
beacon.Parent = root
local phases = {"warmup", "play", "rest"}
local durations = {2, 5, 2}
local colors = {Color3.fromRGB(245, 175, 40), Color3.fromRGB(45, 210, 140), Color3.fromRGB(70, 120, 240)}
local phase = 1
local remaining = durations[phase]
local round = 1
local function display()
    beacon.Color = colors[phase]
    root:SetAttribute("Phase", phases[phase])
    root:SetAttribute("Round", round)
    root:SetAttribute("Remaining", remaining)
end
display()
game:GetService("RunService").Heartbeat:Connect(function(dt)
    remaining = remaining - math.min(math.max(dt, 0), 0.1)
    if remaining <= 0 then
        phase = phase % #phases + 1
        if phase == 1 then round = round + 1 end
        remaining = durations[phase]
    end
    display()
end)
