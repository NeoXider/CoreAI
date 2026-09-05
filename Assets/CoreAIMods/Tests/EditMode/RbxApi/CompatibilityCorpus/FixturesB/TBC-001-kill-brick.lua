local CollectionService = game:GetService("CollectionService")

local brick = Instance.new("Part")
brick.Name = "KillBrick"
brick.Parent = workspace
CollectionService:AddTag(brick, "Kill")

local victim = Instance.new("Part")
victim.Name = "Victim"
victim.Parent = workspace

local humanoid = Instance.new("Humanoid")
humanoid.Parent = workspace

for _, part in ipairs(CollectionService:GetTagged("Kill")) do
    part.Touched:Connect(function(other)
        if other.Name == "Victim" then
            humanoid.Health = 0
        end
    end)
end

humanoid.Died:Connect(function()
    workspace:SetAttribute("TierACorpusResult", "TBC-001-kill-brick")
end)
