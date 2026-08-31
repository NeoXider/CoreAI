local count = 0
workspace.ChildAdded:Once(function(child)
    count = count + 1
    assert(child.Name == "TierAOnceFirst")
    assert(count == 1)
    workspace:SetAttribute("TierACorpusResult", "TAC-005-signal-once")
end)

local first = Instance.new("Folder")
first.Name = "TierAOnceFirst"
first.Parent = workspace

local second = Instance.new("Folder")
second.Name = "TierAOnceSecond"
second.Parent = workspace
