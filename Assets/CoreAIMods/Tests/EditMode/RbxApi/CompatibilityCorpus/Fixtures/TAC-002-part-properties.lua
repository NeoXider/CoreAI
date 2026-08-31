local part = Instance.new("Part")
part.Name = "TierAProperties"
part.Size = Vector3.new(4, 2, 6)
part.CFrame = CFrame.new(3, 5, -7)
part.Color = Color3.fromRGB(64, 128, 255)
part.Transparency = 0.25
part.Anchored = true
part.CanCollide = false
part.Parent = workspace

assert(part.Size == Vector3.new(4, 2, 6))
assert(part.Position == Vector3.new(3, 5, -7))
assert(part.Color == Color3.fromRGB(64, 128, 255))
assert(part.Transparency == 0.25)
assert(part.Anchored == true)
assert(part.CanCollide == false)
workspace:SetAttribute("TierACorpusResult", "TAC-002-part-properties")
