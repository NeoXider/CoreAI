local origin = Vector3.new(0, 20, 0)
local ground = Instance.new("Part")
ground.Name = "Ground"
ground.Parent = workspace

local params = RaycastParams.new()
params.FilterType = Enum.RaycastFilterType.Exclude
params.FilterDescendantsInstances = {}

local result = workspace:Raycast(origin, Vector3.new(0, -50, 0), params)
if result == nil then
    workspace:SetAttribute("TierACorpusResult", "TBC-004-raycast-ground-check")
end
