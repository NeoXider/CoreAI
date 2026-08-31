local base = CFrame.new(5, 10, 0)
local offset = CFrame.new(0, 2, 0)
local world = base:ToWorldSpace(offset)

assert(world.Position == Vector3.new(5, 12, 0))
assert(base * Vector3.new(0, 4, 0) == Vector3.new(5, 14, 0))
assert(base.LookVector == Vector3.new(0, 0, -1))
assert(base:ToObjectSpace(world).Position == Vector3.new(0, 2, 0))
workspace:SetAttribute("TierACorpusResult", "TAC-011-cframe-math")
