--[[@coreai
id: sample_tween_gate
name: Tween Gate
version: 1.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: A gate opens and closes every three seconds. Click its switch to reverse it immediately. The previous tween is cancelled before replacement.
]]
local root = Instance.new("Folder")
root.Name = "TweenGate"
root.Parent = workspace
local gate = Instance.new("Part")
gate.Name = "Gate"
gate.Anchored = true
gate.Size = Vector3.new(5, 5, 0.5)
gate.Position = Vector3.new(0, 2.5, 0)
gate.Color = Color3.fromRGB(55, 120, 215)
gate.Parent = root
local button = Instance.new("Part")
button.Name = "Switch"
button.Anchored = true
button.Size = Vector3.new(1, 1, 1)
button.Position = Vector3.new(4, 1, 0)
button.Color = Color3.fromRGB(240, 175, 45)
button.Parent = root
local click = Instance.new("ClickDetector")
click.MaxActivationDistance = 64
click.Parent = button
local open = false
local activeTween = nil
local elapsed = 0
local transitions = 0
local function toggle()
    if activeTween then activeTween:Cancel() end
    open = not open
    transitions = transitions + 1
    root:SetAttribute("Open", open)
    root:SetAttribute("Transitions", transitions)
    activeTween = game:GetService("TweenService"):Create(gate, TweenInfo.new(0.6), {
        Position = Vector3.new(0, open and 8 or 2.5, 0),
        Transparency = open and 0.65 or 0,
    })
    activeTween:Play()
end
click.MouseClick:Connect(toggle)
root:SetAttribute("Open", false)
root:SetAttribute("Transitions", 0)
game:GetService("RunService").Heartbeat:Connect(function(dt)
    elapsed = elapsed + math.min(dt, 0.1)
    if elapsed >= 3 then
        elapsed = elapsed - 3
        toggle()
    end
end)
