local part = Instance.new("Part")
part.Name = "TierAPropertySource"
part.Parent = workspace

part:GetPropertyChangedSignal("Name"):Connect(function()
    assert(part.Name == "TierAPropertyRenamed")
    workspace:SetAttribute("TierACorpusResult", "TAC-015-script-parent-property-signal")
end)

part.Name = "TierAPropertyRenamed"
