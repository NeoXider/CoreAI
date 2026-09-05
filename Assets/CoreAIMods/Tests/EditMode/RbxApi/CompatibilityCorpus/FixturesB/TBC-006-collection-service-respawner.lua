local CollectionService = game:GetService("CollectionService")
local Debris = game:GetService("Debris")

local spawned = 0
CollectionService:GetInstanceAddedSignal("Temporary"):Connect(function(instance)
    spawned = spawned + 1
    Debris:AddItem(instance, 0.1)
    if spawned == 2 then
        workspace:SetAttribute("TierACorpusResult", "TBC-006-collection-service-respawner")
    end
end)

for index = 1, 2 do
    local part = Instance.new("Part")
    part.Name = "Temp" .. index
    part.Parent = workspace
    CollectionService:AddTag(part, "Temporary")
end
