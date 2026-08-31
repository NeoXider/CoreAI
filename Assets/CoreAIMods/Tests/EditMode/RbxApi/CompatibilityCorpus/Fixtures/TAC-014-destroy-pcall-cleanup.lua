local folder = Instance.new("Folder")
folder.Name = "TierACleanup"
folder.Parent = workspace

local child = Instance.new("Part")
child.Name = "Disposable"
child.Parent = folder
child:Destroy()

assert(folder:FindFirstChild("Disposable") == nil)
local ok = pcall(function()
    child.Parent = folder
end)
assert(ok == false)

folder:Destroy()
assert(workspace:FindFirstChild("TierACleanup") == nil)
workspace:SetAttribute("TierACorpusResult", "TAC-014-destroy-pcall-cleanup")
