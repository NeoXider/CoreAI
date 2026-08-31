local Workspace = game:GetService("Workspace")
local part = Instance.new("Part")
part.Name = "TierAParentLast"
part.Position = Vector3.new(0, 10, 0)
part.Parent = Workspace

assert(Workspace:FindFirstChild("TierAParentLast") == part)
Workspace:SetAttribute("TierACorpusResult", "TAC-001-instance-parent-last")
