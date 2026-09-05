local humanoid = Instance.new("Humanoid")
humanoid.Parent = workspace

local ticks = 0
humanoid.HealthChanged:Connect(function(health)
    ticks = ticks + 1
    if health <= 70 then
        workspace:SetAttribute("TierACorpusResult", "TBC-005-humanoid-damage-loop")
    end
end)

for _ = 1, 3 do
    humanoid:TakeDamage(10)
end
