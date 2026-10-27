--[[@coreai
id: sample_respawning_coins
name: Respawning Coins
version: 1.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Click eight coins to collect them. Each becomes available again after two seconds; the same eight instances are reused.
]]
local root = Instance.new("Folder")
root.Name = "RespawningCoins"
root.Parent = workspace
local coins = {}
local remaining = {}
local collected = 0
local available = 8
for i = 1, 8 do
    local coin = Instance.new("Part")
    coin.Name = "Coin" .. i
    coin.Anchored = true
    coin.Shape = Enum.PartType.Ball
    coin.Size = Vector3.new(0.8, 0.8, 0.8)
    coin.Position = Vector3.new((i - 4.5) * 1.4, 2, 0)
    coin.Material = Enum.Material.Neon
    coin.Color = Color3.fromRGB(255, 200, 35)
    coin.Parent = root
    coins[i] = coin
    remaining[i] = 0
    local index = i
    local click = Instance.new("ClickDetector")
    click.MaxActivationDistance = 64
    click.Parent = coin
    click.MouseClick:Connect(function()
        if remaining[index] > 0 then return end
        remaining[index] = 2
        coin.Transparency = 1
        coin.CanCollide = false
        collected = collected + 1
        available = available - 1
        root:SetAttribute("Collected", collected)
        root:SetAttribute("Available", available)
    end)
end
root:SetAttribute("Collected", 0)
root:SetAttribute("Available", available)
game:GetService("RunService").Heartbeat:Connect(function(dt)
    for i = 1, #coins do
        if remaining[i] > 0 then
            remaining[i] = math.max(0, remaining[i] - math.min(dt, 0.1))
            if remaining[i] == 0 then
                coins[i].Transparency = 0
                coins[i].CanCollide = true
                available = available + 1
                root:SetAttribute("Available", available)
            end
        end
    end
end)
