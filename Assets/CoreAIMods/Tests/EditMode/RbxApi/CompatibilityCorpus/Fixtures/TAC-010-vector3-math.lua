local a: Vector3 = Vector3.new(3, 4, 0)
local b: Vector3 = Vector3.new(0, 0, 2)

assert(a.Magnitude == 5)
assert(a.Unit:FuzzyEq(Vector3.new(0.6, 0.8, 0)))
assert(a:Dot(b) == 0)
assert(a:Cross(b) == Vector3.new(8, -6, 0))
assert(a + b == Vector3.new(3, 4, 2))
workspace:SetAttribute("TierACorpusResult", "TAC-010-vector3-math")
