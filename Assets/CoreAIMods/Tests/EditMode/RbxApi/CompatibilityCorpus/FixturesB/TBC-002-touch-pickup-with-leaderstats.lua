local Players = game:GetService("Players")

local player = Players:GetPlayers()[1]
local leaderstats = Instance.new("Folder")
leaderstats.Name = "leaderstats"
leaderstats.Parent = player

local coins = Instance.new("IntValue")
coins.Name = "Coins"
coins.Value = 0
coins.Parent = leaderstats

coins.Changed:Connect(function(value)
    if value >= 1 then
        workspace:SetAttribute("TierACorpusResult", "TBC-002-touch-pickup-with-leaderstats")
    end
end)

local pickup = Instance.new("Part")
pickup.Name = "Coin"
pickup.Parent = workspace

pickup.Touched:Connect(function()
    coins.Value = coins.Value + 1
    pickup:Destroy()
end)

coins.Value = coins.Value + 1
