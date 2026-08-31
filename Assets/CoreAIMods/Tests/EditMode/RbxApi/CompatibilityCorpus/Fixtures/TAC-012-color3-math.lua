local red = Color3.fromRGB(255, 0, 0)
local blue = Color3.fromRGB(0, 0, 255)
local purple = red:Lerp(blue, 0.5)

assert(red:ToHex() == "FF0000")
assert(math.abs(purple.R - 0.5) < 0.00001)
assert(purple.G == 0)
assert(math.abs(purple.B - 0.5) < 0.00001)
workspace:SetAttribute("TierACorpusResult", "TAC-012-color3-math")
