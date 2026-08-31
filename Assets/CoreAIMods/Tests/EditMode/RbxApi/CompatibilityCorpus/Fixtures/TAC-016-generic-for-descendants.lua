local model = Instance.new("Model")
model.Name = "TierADescendants"

for index = 1, 3 do
    local part = Instance.new("Part")
    part.Name = "Part" .. index
    part.Parent = model
end

model.Parent = workspace
local destroyed = 0
for _, descendant in model:GetDescendants() do
    if descendant:IsA("BasePart") then
        descendant:Destroy()
        destroyed = destroyed + 1
    end
end

assert(destroyed == 3)
assert(#model:GetDescendants() == 0)
workspace:SetAttribute("TierACorpusResult", "TAC-016-generic-for-descendants")
