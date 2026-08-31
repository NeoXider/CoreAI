local part = script.Parent

part:GetPropertyChangedSignal("Name"):Connect(function()
    assert(part.Name == "TierAPropertyRenamed")
    workspace:SetAttribute("TierACorpusResult", "TAC-015-script-parent-property-signal")
end)

part.Name = "TierAPropertyRenamed"
