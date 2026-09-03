--[[@coreai
id: sample_castle3d
name: Castle Showcase (sample)
version: 1.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Opt-in showcase. Builds a medieval castle from every Enum.PartType shape and 25+ Enum.Material values - the reference scene for judging materials, tiling and Part.Color tinting.
]]

-- A materials-and-shapes reference build in the SAME API Roblox uses. Everything is a primitive Part:
-- Block walls, Cylinder tower drums, CornerWedge cone quadrants, Wedge roofs and stairs, Ball finials.
-- Each surface picks the Enum.Material that a real castle would use and tints it with Part.Color, so a
-- screenshot of this mod shows the texture set, its tiling in metres and the Roblox-style colour
-- override side by side. Everything is parented under one Folder the mod owns, so unload destroys it.
-- Deterministic: no randomness, no per-frame work, no input.

local root = Instance.new("Folder")
root.Name = "CastleShowcase"
root.Parent = workspace

local partCount = 0
local materialsSeen = {}
local materialCount = 0
local shapesSeen = {}

-- One primitive. `shape` is optional and defaults to a Block.
local function makePart(name, size, cframe, material, color, shape)
    local part = Instance.new("Part")
    part.Name = "Castle" .. name
    part.Size = size
    part.CFrame = cframe
    part.Material = material
    part.Color = color
    part.Anchored = true
    if shape then
        part.Shape = shape
    end
    part.Parent = root

    partCount = partCount + 1
    local materialKey = tostring(material)
    if not materialsSeen[materialKey] then
        materialsSeen[materialKey] = true
        materialCount = materialCount + 1
    end
    shapesSeen[tostring(part.Shape)] = true
    return part
end

-- A tower drum stands upright: a Roblox Cylinder runs along X, so roll it a quarter turn.
local function makeTower(name, x, z, height, radius, drumMaterial, drumColor, roofMaterial, roofColor)
    makePart(name .. "Drum", Vector3.new(height, radius * 2, radius * 2),
        CFrame.new(x, height / 2, z) * CFrame.Angles(0, 0, math.rad(90)),
        drumMaterial, drumColor, Enum.PartType.Cylinder)

    -- Four CornerWedge quadrants make one cone: each is rotated a quarter turn around Y and pushed
    -- back along its own diagonal, so every peak meets over the middle of the drum.
    local roofHeight = radius * 1.7
    for quadrant = 0, 3 do
        makePart(name .. "Roof" .. quadrant,
            Vector3.new(radius, roofHeight, radius),
            CFrame.new(x, height + roofHeight / 2, z) *
                CFrame.Angles(0, math.rad(90 * quadrant), 0) *
                CFrame.new(-radius / 2, 0, -radius / 2),
            roofMaterial, roofColor, Enum.PartType.CornerWedge)
    end

    makePart(name .. "Finial", Vector3.new(1.1, 1.1, 1.1),
        CFrame.new(x, height + roofHeight + 0.5, z),
        Enum.Material.Metal, Color3.fromRGB(198, 176, 120), Enum.PartType.Ball)

    -- A banner hangs off the drum: thin Block, Fabric, saturated Part.Color over a woven texture.
    makePart(name .. "Banner", Vector3.new(0.2, 5, 2.6),
        CFrame.new(x + radius + 0.2, height * 0.62, z),
        Enum.Material.Fabric, Color3.fromRGB(150, 32, 42))
end

-- ---------------------------------------------------------------- ground, moat and approach
makePart("Ground", Vector3.new(200, 1, 200), CFrame.new(0, -0.5, 0),
    Enum.Material.Grass, Color3.fromRGB(104, 138, 74))
makePart("Bailey", Vector3.new(74, 1, 74), CFrame.new(0, 0.05, 0),
    Enum.Material.Ground, Color3.fromRGB(126, 112, 92))

-- Moat: a rock bed under a translucent Glass slab; the ring is four blocks, not a loop of parts.
local moatSpans = {
    {Vector3.new(84, 1, 8), CFrame.new(0, 0.1, 38)},
    {Vector3.new(84, 1, 8), CFrame.new(0, 0.1, -38)},
    {Vector3.new(8, 1, 68), CFrame.new(38, 0.1, 0)},
    {Vector3.new(8, 1, 68), CFrame.new(-38, 0.1, 0)}
}
for index, span in ipairs(moatSpans) do
    makePart("MoatBed" .. index, span[1], span[2], Enum.Material.Rock, Color3.fromRGB(96, 94, 90))
    local water = makePart("MoatWater" .. index, span[1] + Vector3.new(0, 0.6, 0),
        span[2] * CFrame.new(0, 0.45, 0), Enum.Material.Glass, Color3.fromRGB(58, 104, 132))
    water.Transparency = 0.45
end

makePart("Road", Vector3.new(9, 0.4, 40), CFrame.new(0, 0.25, -58),
    Enum.Material.Asphalt, Color3.fromRGB(88, 86, 84))
makePart("Path", Vector3.new(9, 0.4, 24), CFrame.new(0, 0.3, -30),
    Enum.Material.Sand, Color3.fromRGB(206, 186, 142))
makePart("PathGravel", Vector3.new(13, 0.3, 8), CFrame.new(0, 0.32, -44),
    Enum.Material.Pebble, Color3.fromRGB(158, 152, 143))

-- Drawbridge over the moat: planks, two beams and rusted chains.
makePart("Bridge", Vector3.new(8, 0.5, 14), CFrame.new(0, 1.1, -38),
    Enum.Material.WoodPlanks, Color3.fromRGB(140, 104, 66))
makePart("BridgeBeamL", Vector3.new(0.8, 0.8, 14), CFrame.new(-3.6, 0.9, -38),
    Enum.Material.Wood, Color3.fromRGB(104, 76, 48))
makePart("BridgeBeamR", Vector3.new(0.8, 0.8, 14), CFrame.new(3.6, 0.9, -38),
    Enum.Material.Wood, Color3.fromRGB(104, 76, 48))
makePart("ChainL", Vector3.new(0.35, 9, 0.35), CFrame.new(-3.6, 6, -33) * CFrame.Angles(math.rad(20), 0, 0),
    Enum.Material.CorrodedMetal, Color3.fromRGB(122, 88, 62))
makePart("ChainR", Vector3.new(0.35, 9, 0.35), CFrame.new(3.6, 6, -33) * CFrame.Angles(math.rad(20), 0, 0),
    Enum.Material.CorrodedMetal, Color3.fromRGB(122, 88, 62))

-- ---------------------------------------------------------------- curtain walls and merlons
local wallHeight = 11
local wallSpans = {
    {"North", Vector3.new(64, wallHeight, 3), CFrame.new(0, wallHeight / 2, 32)},
    {"East", Vector3.new(3, wallHeight, 64), CFrame.new(32, wallHeight / 2, 0)},
    {"West", Vector3.new(3, wallHeight, 64), CFrame.new(-32, wallHeight / 2, 0)},
    {"SouthLeft", Vector3.new(26, wallHeight, 3), CFrame.new(-19, wallHeight / 2, -32)},
    {"SouthRight", Vector3.new(26, wallHeight, 3), CFrame.new(19, wallHeight / 2, -32)}
}
for _, span in ipairs(wallSpans) do
    makePart("Wall" .. span[1], span[2], span[3], Enum.Material.Cobblestone, Color3.fromRGB(146, 141, 132))
end

makePart("PlinthNorth", Vector3.new(66, 2, 5), CFrame.new(0, 1, 32),
    Enum.Material.Granite, Color3.fromRGB(120, 118, 116))
makePart("PlinthSouthL", Vector3.new(28, 2, 5), CFrame.new(-19, 1, -32),
    Enum.Material.Granite, Color3.fromRGB(120, 118, 116))
makePart("PlinthSouthR", Vector3.new(28, 2, 5), CFrame.new(19, 1, -32),
    Enum.Material.Granite, Color3.fromRGB(120, 118, 116))

-- Merlons: alternating teeth along the north and south walk.
for step = -6, 6 do
    local x = step * 5
    makePart("MerlonN" .. (step + 6), Vector3.new(3, 2.2, 3),
        CFrame.new(x, wallHeight + 1.1, 32), Enum.Material.Slate, Color3.fromRGB(122, 124, 128))
    if math.abs(x) > 7 then
        makePart("MerlonS" .. (step + 6), Vector3.new(3, 2.2, 3),
            CFrame.new(x, wallHeight + 1.1, -32), Enum.Material.Slate, Color3.fromRGB(122, 124, 128))
    end
end

-- ---------------------------------------------------------------- corner towers
makeTower("TowerNE", 32, 32, 20, 5, Enum.Material.Limestone, Color3.fromRGB(196, 186, 162),
    Enum.Material.Slate, Color3.fromRGB(78, 84, 96))
makeTower("TowerNW", -32, 32, 20, 5, Enum.Material.Limestone, Color3.fromRGB(196, 186, 162),
    Enum.Material.ClayRoofTiles, Color3.fromRGB(158, 78, 54))
makeTower("TowerSE", 32, -32, 18, 5, Enum.Material.Sandstone, Color3.fromRGB(202, 172, 126),
    Enum.Material.RoofShingles, Color3.fromRGB(112, 82, 58))
makeTower("TowerSW", -32, -32, 22, 5, Enum.Material.Basalt, Color3.fromRGB(92, 92, 98),
    Enum.Material.Slate, Color3.fromRGB(78, 84, 96))

-- The north-west tower carries winter dressing so Snow and Ice are visible side by side.
makePart("TowerSnowCap", Vector3.new(9, 0.6, 9), CFrame.new(-32, 20.1, 32),
    Enum.Material.Snow, Color3.fromRGB(238, 242, 248))
makePart("TowerIcicle", Vector3.new(1.2, 3, 1.2), CFrame.new(-27.5, 18.4, 32),
    Enum.Material.Ice, Color3.fromRGB(186, 214, 232))

-- ---------------------------------------------------------------- gatehouse
makePart("GateTowerL", Vector3.new(8, 15, 8), CFrame.new(-7, 7.5, -32),
    Enum.Material.Brick, Color3.fromRGB(150, 92, 74))
makePart("GateTowerR", Vector3.new(8, 15, 8), CFrame.new(7, 7.5, -32),
    Enum.Material.Brick, Color3.fromRGB(150, 92, 74))
makePart("GateLintel", Vector3.new(14, 3, 6), CFrame.new(0, 12.5, -32),
    Enum.Material.Limestone, Color3.fromRGB(196, 186, 162))
-- Two Wedges facing each other read as the arch over the gate.
makePart("GateArchL", Vector3.new(4, 4, 5), CFrame.new(-4, 9, -32) * CFrame.Angles(0, math.rad(180), 0),
    Enum.Material.Sandstone, Color3.fromRGB(202, 172, 126), Enum.PartType.Wedge)
makePart("GateArchR", Vector3.new(4, 4, 5), CFrame.new(4, 9, -32),
    Enum.Material.Sandstone, Color3.fromRGB(202, 172, 126), Enum.PartType.Wedge)
makePart("GateThreshold", Vector3.new(8, 0.4, 6), CFrame.new(0, 0.4, -32),
    Enum.Material.Pavement, Color3.fromRGB(148, 144, 138))
-- Portcullis: thin Cylinder bars behind the arch, plus a plate frame.
for bar = 0, 5 do
    makePart("Portcullis" .. bar, Vector3.new(8.5, 0.4, 0.4),
        CFrame.new(-3.4 + bar * 1.36, 4.5, -30.6) * CFrame.Angles(0, 0, math.rad(90)),
        Enum.Material.Metal, Color3.fromRGB(84, 86, 92), Enum.PartType.Cylinder)
end
makePart("PortcullisFrame", Vector3.new(9.4, 0.6, 0.6), CFrame.new(0, 9, -30.6),
    Enum.Material.DiamondPlate, Color3.fromRGB(112, 114, 120))
-- Neon torches read as light sources without a Light instance.
makePart("TorchL", Vector3.new(1, 1, 1), CFrame.new(-6, 7, -29.6),
    Enum.Material.Neon, Color3.fromRGB(255, 156, 62), Enum.PartType.Ball)
makePart("TorchR", Vector3.new(1, 1, 1), CFrame.new(6, 7, -29.6),
    Enum.Material.Neon, Color3.fromRGB(255, 156, 62), Enum.PartType.Ball)

-- ---------------------------------------------------------------- courtyard, keep and props
makePart("Courtyard", Vector3.new(30, 0.4, 30), CFrame.new(0, 0.35, 4),
    Enum.Material.Marble, Color3.fromRGB(216, 212, 204))
makePart("CourtyardTiles", Vector3.new(12, 0.45, 12), CFrame.new(0, 0.4, 4),
    Enum.Material.CeramicTiles, Color3.fromRGB(196, 206, 210))

makePart("Keep", Vector3.new(22, 20, 18), CFrame.new(0, 10, 10),
    Enum.Material.Brick, Color3.fromRGB(154, 98, 78))
makePart("KeepPlinth", Vector3.new(24, 2, 20), CFrame.new(0, 1, 10),
    Enum.Material.Granite, Color3.fromRGB(120, 118, 116))
makePart("KeepInnerWall", Vector3.new(20, 18, 0.6), CFrame.new(0, 10, 0.9),
    Enum.Material.Plaster, Color3.fromRGB(224, 214, 196))
-- Two Wedges back to back make the gabled roof.
makePart("KeepRoofL", Vector3.new(11, 7, 18), CFrame.new(-5.5, 23.5, 10) * CFrame.Angles(0, math.rad(180), 0),
    Enum.Material.RoofShingles, Color3.fromRGB(104, 74, 54), Enum.PartType.Wedge)
makePart("KeepRoofR", Vector3.new(11, 7, 18), CFrame.new(5.5, 23.5, 10),
    Enum.Material.RoofShingles, Color3.fromRGB(104, 74, 54), Enum.PartType.Wedge)
makePart("Chimney", Vector3.new(6, 2.4, 2.4), CFrame.new(7, 27, 14) * CFrame.Angles(0, 0, math.rad(90)),
    Enum.Material.Concrete, Color3.fromRGB(158, 156, 150), Enum.PartType.Cylinder)
for window = 0, 2 do
    local pane = makePart("KeepWindow" .. window, Vector3.new(2.6, 4, 0.4),
        CFrame.new(-7 + window * 7, 12, 0.7), Enum.Material.Glass, Color3.fromRGB(150, 192, 208))
    pane.Transparency = 0.35
end

-- Stairs up to the keep: four Wedge steps, each one step deeper.
for step = 0, 3 do
    makePart("Stair" .. step, Vector3.new(10, 1.2, 3),
        CFrame.new(0, 0.8 + step * 1.2, -1.5 - step * 3) * CFrame.Angles(0, math.rad(180), 0),
        Enum.Material.Sandstone, Color3.fromRGB(198, 174, 136), Enum.PartType.Wedge)
end

-- Well: a rock ring with a wooden roof on two posts.
makePart("WellRing", Vector3.new(2.4, 6, 6), CFrame.new(-14, 1.2, -8) * CFrame.Angles(0, 0, math.rad(90)),
    Enum.Material.Rock, Color3.fromRGB(128, 124, 118), Enum.PartType.Cylinder)
makePart("WellPostL", Vector3.new(0.5, 5, 0.5), CFrame.new(-16.6, 3.5, -8),
    Enum.Material.Wood, Color3.fromRGB(112, 82, 52))
makePart("WellPostR", Vector3.new(0.5, 5, 0.5), CFrame.new(-11.4, 3.5, -8),
    Enum.Material.Wood, Color3.fromRGB(112, 82, 52))
makePart("WellRoofL", Vector3.new(3.2, 2, 6), CFrame.new(-15.6, 7, -8) * CFrame.Angles(0, math.rad(180), 0),
    Enum.Material.RoofShingles, Color3.fromRGB(96, 70, 50), Enum.PartType.Wedge)
makePart("WellRoofR", Vector3.new(3.2, 2, 6), CFrame.new(-12.4, 7, -8),
    Enum.Material.RoofShingles, Color3.fromRGB(96, 70, 50), Enum.PartType.Wedge)

-- Courtyard props: a brazier of cooled lava, a rug, a leather-topped bench, crates and a mud patch.
makePart("Brazier", Vector3.new(1.6, 3, 3), CFrame.new(12, 0.8, -8) * CFrame.Angles(0, 0, math.rad(90)),
    Enum.Material.Metal, Color3.fromRGB(76, 74, 72), Enum.PartType.Cylinder)
makePart("BrazierCoals", Vector3.new(2.2, 2.2, 2.2), CFrame.new(12, 2.4, -8),
    Enum.Material.CrackedLava, Color3.fromRGB(226, 108, 48), Enum.PartType.Ball)
makePart("Rug", Vector3.new(8, 0.15, 5), CFrame.new(0, 0.6, -6),
    Enum.Material.Carpet, Color3.fromRGB(126, 52, 58))
makePart("BenchSeat", Vector3.new(5, 0.4, 1.6), CFrame.new(-8, 1.6, -14),
    Enum.Material.Leather, Color3.fromRGB(112, 74, 48))
makePart("BenchLegL", Vector3.new(0.4, 1.4, 1.4), CFrame.new(-10, 0.9, -14),
    Enum.Material.Wood, Color3.fromRGB(104, 76, 48))
makePart("BenchLegR", Vector3.new(0.4, 1.4, 1.4), CFrame.new(-6, 0.9, -14),
    Enum.Material.Wood, Color3.fromRGB(104, 76, 48))
makePart("Crate", Vector3.new(2, 2, 2), CFrame.new(9, 1.4, 14),
    Enum.Material.Cardboard, Color3.fromRGB(178, 142, 96))
makePart("CrateMat", Vector3.new(2.6, 0.2, 2.6), CFrame.new(9, 0.5, 14),
    Enum.Material.Rubber, Color3.fromRGB(58, 58, 60))
makePart("MudPatch", Vector3.new(7, 0.3, 5), CFrame.new(14, 0.4, 8),
    Enum.Material.Mud, Color3.fromRGB(94, 76, 58))
makePart("HerbBed", Vector3.new(6, 0.4, 4), CFrame.new(-14, 0.4, 14),
    Enum.Material.LeafyGrass, Color3.fromRGB(88, 122, 62))
makePart("FoilPennant", Vector3.new(0.15, 1.6, 2.4), CFrame.new(0, 26, 1.2),
    Enum.Material.Foil, Color3.fromRGB(226, 224, 214))

local shapeCount = 0
for _ in pairs(shapesSeen) do
    shapeCount = shapeCount + 1
end

print("[CastleShowcase] parts=" .. partCount ..
    " materials=" .. materialCount ..
    " shapes=" .. shapeCount)
