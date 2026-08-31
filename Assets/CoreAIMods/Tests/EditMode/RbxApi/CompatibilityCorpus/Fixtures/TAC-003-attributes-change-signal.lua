local part = Instance.new("Part")
part.Name = "TierAAttributes"
part.Parent = workspace
part:SetAttribute("Health", 100)

part:GetAttributeChangedSignal("Health"):Connect(function()
    assert(part:GetAttribute("Health") == 75)
    local attributes = part:GetAttributes()
    assert(attributes.Health == 75)
    workspace:SetAttribute("TierACorpusResult", "TAC-003-attributes-change-signal")
end)

part:SetAttribute("Health", 75)
