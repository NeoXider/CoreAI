--[[@coreai
id: sample_orbit_gallery
name: Orbit Gallery
version: 1.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Twelve orbiting lights, frame-independent movement and a clickable pause control. No per-frame instance creation.
]]
local root = Instance.new("Folder")
root.Name = "OrbitGallery"
root.Parent = workspace
local count = 12
local elapsed = 0
local paused = false
local planets = {}
for i = 1, count do
    local part = Instance.new("Part")
    part.Name = "Orb" .. i
    part.Anchored = true
    part.Shape = Enum.PartType.Ball
    part.Size = Vector3.new(0.6, 0.6, 0.6)
    part.Material = Enum.Material.Neon
    part.Color = Color3.fromRGB(40 + i * 15, 180, 245)
    part.Parent = root
    planets[i] = part
end
local button = Instance.new("Part")
button.Name = "PauseButton"
button.Anchored = true
button.Position = Vector3.new(0, 1, 0)
button.Size = Vector3.new(2, 0.5, 2)
button.Color = Color3.fromRGB(40, 200, 150)
button.Parent = root
local click = Instance.new("ClickDetector")
click.MaxActivationDistance = 64
click.Parent = button
click.MouseClick:Connect(function()
    paused = not paused
    root:SetAttribute("Paused", paused)
end)
root:SetAttribute("Paused", false)
root:SetAttribute("OrbCount", count)
root:SetAttribute("Elapsed", elapsed)
game:GetService("RunService").Heartbeat:Connect(function(dt)
    if paused then return end
    elapsed = elapsed + math.min(dt, 0.1)
    for i = 1, count do
        local angle = elapsed * (0.25 + i * 0.035) + i * math.pi * 2 / count
        local radius = 2 + (i % 3) * 1.2
        planets[i].Position = Vector3.new(math.cos(angle) * radius, 2 + (i % 4) * 0.65, math.sin(angle) * radius)
    end
    root:SetAttribute("Elapsed", elapsed)
end)
