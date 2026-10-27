--[[@coreai
id: sample_saved_score
name: Saved Score
version: 1.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Click a block to increase a mod-scoped score. Reload reads it back from the configured mod state store; this is separate from saving source code.
]]
local root = Instance.new("Folder")
root.Name = "SavedScore"
root.Parent = workspace
local score = math.max(0, tonumber(store_get("score")) or 0)
local block = Instance.new("Part")
block.Name = "ScoreButton"
block.Anchored = true
block.Position = Vector3.new(0, 2, 0)
block.Size = Vector3.new(2, 2, 2)
block.Color = Color3.fromRGB(45, 180, 240)
block.Parent = root
local function display()
    root:SetAttribute("Score", score)
    block.Size = Vector3.new(2, 2 + math.min(score, 20) * 0.1, 2)
end
local click = Instance.new("ClickDetector")
click.MaxActivationDistance = 64
click.Parent = block
click.MouseClick:Connect(function()
    score = score + 1
    store_set("score", tostring(score))
    display()
end)
display()
