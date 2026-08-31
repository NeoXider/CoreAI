task.delay(0, function()
    local child = Instance.new("Folder")
    child.Name = "TierADelayedChild"
    child.Parent = workspace
end)

local child = workspace:WaitForChild("TierADelayedChild")
assert(child.Name == "TierADelayedChild")
workspace:SetAttribute("TierACorpusResult", "TAC-017-waitforchild-yield")
