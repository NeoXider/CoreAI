-- Model-authored execute_lua calls in original order.
-- Model: opencode/space-bunny-free; scenario: g6_free_build_vision; calls: 4
-- pcall keeps later calls running after a failed section, matching the benchmark.
local function run_chunk(index, body)
    local ok, err = pcall(body)
    if not ok then print('G6 chunk ' .. index .. ' failed: ' .. tostring(err)) end
end

run_chunk(1, function()
local function part(name, size, cframe, material, color, shape)
  local p = Instance.new('Part')
  p.Name = name
  p.Size = size
  p.CFrame = cframe
  p.Material = material
  p.Color = color
  p.Shape = shape
  p.Anchored = true
  p.Parent = workspace
end

part('CastleGround', Vector3.new(132, 1, 132), CFrame.new(0, -0.5, 0), Enum.Material.Grass, Color3.fromRGB(205, 224, 170), Enum.PartType.Block)
part('CastleMoatNorth', Vector3.new(108, 0.25, 8), CFrame.new(0, 0.08, 43), Enum.Material.Glass, Color3.fromRGB(82, 171, 207), Enum.PartType.Block)
part('CastleMoatSouth', Vector3.new(108, 0.25, 8), CFrame.new(0, 0.08, -43), Enum.Material.Glass, Color3.fromRGB(82, 171, 207), Enum.PartType.Block)
part('CastleMoatWest', Vector3.new(8, 0.25, 78), CFrame.new(-51, 0.08, 0), Enum.Material.Glass, Color3.fromRGB(82, 171, 207), Enum.PartType.Block)
part('CastleMoatEast', Vector3.new(8, 0.25, 78), CFrame.new(51, 0.08, 0), Enum.Material.Glass, Color3.fromRGB(82, 171, 207), Enum.PartType.Block)
part('CastleWallNorth', Vector3.new(88, 10, 3), CFrame.new(0, 5, 34), Enum.Material.Cobblestone, Color3.fromRGB(226, 224, 214), Enum.PartType.Block)
part('CastleWallSouthWest', Vector3.new(32, 10, 3), CFrame.new(-26, 5, -34), Enum.Material.Cobblestone, Color3.fromRGB(226, 224, 214), Enum.PartType.Block)
part('CastleWallSouthEast', Vector3.new(32, 10, 3), CFrame.new(26, 5, -34), Enum.Material.Cobblestone, Color3.fromRGB(226, 224, 214), Enum.PartType.Block)
part('CastleWallWest', Vector3.new(3, 10, 68), CFrame.new(-42, 5, 0), Enum.Material.Cobblestone, Color3.fromRGB(226, 224, 214), Enum.PartType.Block)
part('CastleWallEast', Vector3.new(3, 10, 68), CFrame.new(42, 5, 0), Enum.Material.Cobblestone, Color3.fromRGB(226, 224, 214), Enum.PartType.Block)
part('CastleTowerNW', Vector3.new(12, 10, 10), CFrame.new(-42, 7, 34) * CFrame.Angles(0, 0, math.rad(90)), Enum.Material.Granite, Color3.fromRGB(228, 226, 218), Enum.PartType.Cylinder)
part('CastleTowerNE', Vector3.new(12, 10, 10), CFrame.new(42, 7, 34) * CFrame.Angles(0, 0, math.rad(90)), Enum.Material.Granite, Color3.fromRGB(228, 226, 218), Enum.PartType.Cylinder)
part('CastleTowerSW', Vector3.new(12, 10, 10), CFrame.new(-42, 7, -34) * CFrame.Angles(0, 0, math.rad(90)), Enum.Material.Granite, Color3.fromRGB(228, 226, 218), Enum.PartType.Cylinder)
part('CastleTowerSE', Vector3.new(12, 10, 10), CFrame.new(42, 7, -34) * CFrame.Angles(0, 0, math.rad(90)), Enum.Material.Granite, Color3.fromRGB(228, 226, 218), Enum.PartType.Cylinder)
part('CastleGateTowerWest', Vector3.new(14, 12, 11), CFrame.new(-10, 8, 34) * CFrame.Angles(0, 0, math.rad(90)), Enum.Material.Sandstone, Color3.fromRGB(235, 218, 184), Enum.PartType.Cylinder)
part('CastleGateTowerEast', Vector3.new(14, 12, 11), CFrame.new(10, 8, 34) * CFrame.Angles(0, 0, math.rad(90)), Enum.Material.Sandstone, Color3.fromRGB(235, 218, 184), Enum.PartType.Cylinder)
part('CastleGateLintel', Vector3.new(20, 7, 5), CFrame.new(0, 10.5, 34), Enum.Material.Basalt, Color3.fromRGB(225, 225, 226), Enum.PartType.Block)
part('CastleKeepBase', Vector3.new(32, 24, 24), CFrame.new(0, 12, -10), Enum.Material.Limestone, Color3.fromRGB(239, 232, 211), Enum.PartType.Block)
part('CastleKeepUpper', Vector3.new(24, 10, 18), CFrame.new(0, 29, -10), Enum.Material.Marble, Color3.fromRGB(240, 238, 229), Enum.PartType.Block)
part('CastleKeepRoof', Vector3.new(28, 2, 22), CFrame.new(0, 35, -10), Enum.Material.Slate, Color3.fromRGB(224, 227, 231), Enum.PartType.Block)
report('Castle foundations, curtain walls, corner towers, gatehouse, and keep created.')
end)

run_chunk(2, function()
local function part(name, size, cframe, material, color, shape)
  local p = Instance.new('Part')
  p.Name = name
  p.Size = size
  p.CFrame = cframe
  p.Material = material
  p.Color = color
  p.Shape = shape
  p.Anchored = true
  p.Parent = workspace
end

for _, x in ipairs({-40, -30, -20, -10, 10, 20, 30, 40}) do
  part('CastleNorthMerlon' .. x, Vector3.new(4, 2, 4), CFrame.new(x, 11, 34), Enum.Material.Granite, Color3.fromRGB(230, 228, 220), Enum.PartType.Block)
end
for _, x in ipairs({-36, -24, -12, 12, 24, 36}) do
  part('CastleSouthMerlon' .. x, Vector3.new(4, 2, 4), CFrame.new(x, 11, -34), Enum.Material.Granite, Color3.fromRGB(230, 228, 220), Enum.PartType.Block)
end
for _, x in ipairs({-42, 42}) do
  for _, z in ipairs({-20, 20}) do
    local side = x < 0 and 'West' or 'East'
    part('Castle' .. side .. 'Merlon' .. (z < 0 and 'S' or 'N'), Vector3.new(4, 2, 4), CFrame.new(x, 11, z), Enum.Material.Granite, Color3.fromRGB(230, 228, 220), Enum.PartType.Block)
  end
end
report('Castle battlements completed along all curtain walls.')
end)

run_chunk(3, function()
local function part(name, size, cframe, material, color, shape)
  local p = Instance.new('Part')
  p.Name = name
  p.Size = size
  p.CFrame = cframe
  p.Material = material
  p.Color = color
  p.Shape = shape
  p.Anchored = true
  p.Parent = workspace
end

part('CastleKeepGableWest', Vector3.new(15, 1.5, 24), CFrame.new(-7, 38.2, -10) * CFrame.Angles(0, 0, math.rad(-30)), Enum.Material.Slate, Color3.fromRGB(224, 228, 232), Enum.PartType.Wedge)
part('CastleKeepGableEast', Vector3.new(15, 1.5, 24), CFrame.new(7, 38.2, -10) * CFrame.Angles(0, 0, math.rad(30)), Enum.Material.Slate, Color3.fromRGB(224, 228, 232), Enum.PartType.Wedge)
part('CastleKeepRidge', Vector3.new(1, 1, 25), CFrame.new(0, 44, -10), Enum.Material.Metal, Color3.fromRGB(232, 232, 232), Enum.PartType.Cylinder)
for _, x in ipairs({-11, -8, -5, -2, 1, 4, 7, 10}) do
  part('CastleKeepNorthMerlon' .. x, Vector3.new(2.2, 2.5, 2.2), CFrame.new(x, 36.1, 1.5), Enum.Material.Granite, Color3.fromRGB(230, 228, 220), Enum.PartType.Block)
end
for _, x in ipairs({-10, -6, -2, 2, 6, 10}) do
  part('CastleKeepSouthMerlon' .. x, Vector3.new(2.2, 2.5, 2.2), CFrame.new(x, 36.1, -21.5), Enum.Material.Granite, Color3.fromRGB(230, 228, 220), Enum.PartType.Block)
end
part('CastleKeepWindowWest', Vector3.new(3.2, 6, 0.3), CFrame.new(-5, 28.5, 0.9), Enum.Material.Glass, Color3.fromRGB(95, 172, 210), Enum.PartType.Block)
part('CastleKeepWindowEast', Vector3.new(3.2, 6, 0.3), CFrame.new(5, 28.5, 0.9), Enum.Material.Glass, Color3.fromRGB(95, 172, 210), Enum.PartType.Block)
report('Castle keep gained a pitched slate roof, metal ridge, lancet windows, and crown battlements.')
end)

run_chunk(4, function()
local function part(name, size, cframe, material, color, shape)
  local p = Instance.new('Part')
  p.Name = name
  p.Size = size
  p.CFrame = cframe
  p.Material = material
  p.Color = color
  p.Shape = shape
  p.Anchored = true
  p.Parent = workspace
end

part('CastleGateArchLeftPost', Vector3.new(1.4, 7, 1.4), CFrame.new(-4.2, 3.5, 36.9), Enum.Material.Sandstone, Color3.fromRGB(235, 218, 184), Enum.PartType.Block)
part('CastleGateArchRightPost', Vector3.new(1.4, 7, 1.4), CFrame.new(4.2, 3.5, 36.9), Enum.Material.Sandstone, Color3.fromRGB(235, 218, 184), Enum.PartType.Block)
part('CastleGateArchLeftWedge', Vector3.new(4.2, 4.2, 1.4), CFrame.new(-3.25, 6.1, 36.9) * CFrame.Angles(0, 0, math.rad(45)), Enum.Material.Sandstone, Color3.fromRGB(235, 218, 184), Enum.PartType.Wedge)
part('CastleGateArchRightWedge', Vector3.new(4.2, 4.2, 1.4), CFrame.new(3.25, 6.1, 36.9) * CFrame.Angles(0, 0, math.rad(-45)), Enum.Material.Sandstone, Color3.fromRGB(235, 218, 184), Enum.PartType.Wedge)
part('CastleGateArchKeystone', Vector3.new(2.6, 2, 1.6), CFrame.new(0, 7.35, 36.9), Enum.Material.Limestone, Color3.fromRGB(239, 232, 211), Enum.PartType.Block)

for i = -2, 2 do
  part('CastlePortcullisBar' .. (i + 3), Vector3.new(7, 0.38, 0.38), CFrame.new(i * 1.55, 4, 35.2) * CFrame.Angles(0, 0, math.rad(90)), Enum.Material.CorrodedMetal, Color3.fromRGB(225, 211, 190), Enum.PartType.Cylinder)
  part('CastlePortcullisSpike' .. (i + 3), Vector3.new(0.65, 1.4, 0.65), CFrame.new(i * 1.55, 0.35, 35.2) * CFrame.Angles(math.rad(180), 0, 0), Enum.Material.Metal, Color3.fromRGB(226, 226, 226), Enum.PartType.Wedge)
end
part('CastlePortcullisRailLow', Vector3.new(8.2, 0.45, 0.45), CFrame.new(0, 2.4, 35.2), Enum.Material.CorrodedMetal, Color3.fromRGB(225, 211, 190), Enum.PartType.Cylinder)
part('CastlePortcullisRailHigh', Vector3.new(8.2, 0.45, 0.45), CFrame.new(0, 5.6, 35.2), Enum.Material.CorrodedMetal, Color3.fromRGB(225, 211, 190), Enum.PartType.Cylinder)
part('CastleDrawbridgeDeck', Vector3.new(10, 1, 20), CFrame.new(0, 0.65, 46.5) * CFrame.Angles(math.rad(1.5), 0, 0), Enum.Material.WoodPlanks, Color3.fromRGB(230, 207, 167), Enum.PartType.Block)
part('CastleDrawbridgeHinge', Vector3.new(10.8, 0.9, 0.9), CFrame.new(0, 0.75, 36.8), Enum.Material.CorrodedMetal, Color3.fromRGB(225, 211, 190), Enum.PartType.Cylinder)
report('Castle gate arch, iron portcullis, and timber drawbridge completed.')
end)
