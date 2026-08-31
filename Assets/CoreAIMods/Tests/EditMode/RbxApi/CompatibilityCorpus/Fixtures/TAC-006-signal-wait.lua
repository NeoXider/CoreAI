task.spawn(function()
    local child = workspace.ChildAdded:Wait()
    assert(child.Name == "TierAWaitedChild")
    workspace:SetAttribute("TierACorpusResult", "TAC-006-signal-wait")
end)

local child = Instance.new("Folder")
child.Name = "TierAWaitedChild"
child.Parent = workspace
