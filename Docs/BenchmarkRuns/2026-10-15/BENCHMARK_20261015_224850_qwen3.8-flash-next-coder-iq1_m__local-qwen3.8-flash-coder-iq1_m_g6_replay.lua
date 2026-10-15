-- Model-authored execute_lua calls in original order.
-- Model: local/qwen3.8-flash-coder-iq1_m; scenario: g6_free_build_vision; calls: 14
-- pcall keeps later calls running after a failed section, matching the benchmark.
local function run_chunk(index, body)
    local ok, err = pcall(body)
    if not ok then print('G6 chunk ' .. index .. ' failed: ' .. tostring(err)) end
end

run_chunk(1, function()

local W = workspace
local function add(n, s, c, m, col, sh)
  local p = Instance.new('Part')
  p.Name = n
  p.Size = s
  p.CFrame = c
  p.Material = m
  if col then p.Color = col end
  p.Shape = sh
  p.Anchored = true
  p.Parent = W
end

local stone = Color3.fromRGB(214, 208, 196)
local stoneDark = Color3.fromRGB(198, 190, 178)

add('CastleGround', Vector3.new(126, 2, 126), CFrame.new(0, -1, 0), Enum.Material.LeafyGrass, Color3.fromRGB(206, 218, 190), Enum.PartType.Block)
add('CastleWallNorthWest', Vector3.new(36, 11, 3), CFrame.new(-24, 5.5, -32.5), Enum.Material.Cobblestone, stone, Enum.PartType.Block)
add('CastleWallNorthEast', Vector3.new(36, 11, 3), CFrame.new(24, 5.5, -32.5), Enum.Material.Cobblestone, stone, Enum.PartType.Block)
add('CastleWallSouth', Vector3.new(78, 11, 3), CFrame.new(0, 5.5, 32.5), Enum.Material.Cobblestone, Color3.fromRGB(222, 216, 204), Enum.PartType.Block)
add('CastleWallWest', Vector3.new(3, 11, 65), CFrame.new(-40.5, 5.5, 1.5), Enum.Material.Cobblestone, stone, Enum.PartType.Block)
add('CastleWallEast', Vector3.new(3, 11, 65), CFrame.new(40.5, 5.5, 1.5), Enum.Material.Cobblestone, stone, Enum.PartType.Block)
add('CastleGateLintel', Vector3.new(12, 4, 4), CFrame.new(0, 13, -32.5), Enum.Material.Granite, Color3.fromRGB(220, 216, 208), Enum.PartType.Block)
add('CastlePortcullisBar1', Vector3.new(1, 11, 1), CFrame.new(-4.2, 5.5, -32.5), Enum.Material.CorrodedMetal, Color3.fromRGB(196, 190, 186), Enum.PartType.Block)
add('CastlePortcullisBar2', Vector3.new(1, 11, 1), CFrame.new(-1.4, 5.5, -32.5), Enum.Material.CorrodedMetal, Color3.fromRGB(196, 190, 186), Enum.PartType.Block)
add('CastlePortcullisBar3', Vector3.new(1, 11, 1), CFrame.new(1.4, 5.5, -32.5), Enum.Material.CorrodedMetal, Color3.fromRGB(196, 190, 186), Enum.PartType.Block)
add('CastlePortcullisBar4', Vector3.new(1, 11, 1), CFrame.new(4.2, 5.5, -32.5), Enum.Material.CorrodedMetal, Color3.fromRGB(196, 190, 186), Enum.PartType.Block)
add('CastlePortcullisCrossbar', Vector3.new(11, 1.2, 1.2), CFrame.new(0, 8.4, -32.5), Enum.Material.Metal, Color3.fromRGB(204, 200, 196), Enum.PartType.Block)
add('CastleGateArchLeft', Vector3.new(2, 2, 4), CFrame.new(-5, 11, -32.5), Enum.Material.Granite, Color3.fromRGB(210, 204, 196), Enum.PartType.Block)
add('CastleGateArchRight', Vector3.new(2, 2, 4), CFrame.new(5, 11, -32.5), Enum.Material.Granite, Color3.fromRGB(210, 204, 196), Enum.PartType.Block)
return 'section A ok'

end)

run_chunk(2, function()

local W = workspace
local function add(n, s, c, m, col, sh)
  local p = Instance.new('Part')
  p.Name = n; p.Size = s; p.CFrame = c; p.Material = m
  if col then p.Color = col end
  p.Shape = sh; p.Anchored = true; p.Parent = W
end
local st = Color3.fromRGB(216, 210, 198)
local st2 = Color3.fromRGB(204, 198, 186)

-- corner tower drums (cylinders run along X -> rotate 90 about Z for vertical)
add('CastleTowerNWDrum', Vector3.new(17, 10.5, 10.5), CFrame.new(-41, 8.5, -33.5) * CFrame.angles(0,0,math.rad(90)), Enum.Material.Limestone, st, Enum.PartType.Cylinder)
add('CastleTowerNEDrum', Vector3.new(16, 10, 10), CFrame.new(41, 8, -33.5) * CFrame.angles(0,0,math.rad(90)), Enum.Material.Limestone, st2, Enum.PartType.Cylinder)
add('CastleTowerSWDrum', Vector3.new(19, 11.5, 11.5), CFrame.new(-41, 9.5, 33.5) * CFrame.angles(0,0,math.rad(90)), Enum.Material.Sandstone, Color3.fromRGB(226,216,196), Enum.PartType.Cylinder)
add('CastleTowerSEDrum', Vector3.new(15.5, 9.5, 9.5), CFrame.new(41, 7.75, 33.5) * CFrame.angles(0,0,math.rad(90)), Enum.Material.Sandstone, Color3.fromRGB(226,216,196), Enum.PartType.Cylinder)
-- mid-towers on the curtain
add('CastleTowerWDrum', Vector3.new(16.5, 9, 9), CFrame.new(-40.5, 8.25, -6) * CFrame.angles(0,0,math.rad(90)), Enum.Material.Limestone, st, Enum.PartType.Cylinder)
add('CastleTowerEDrum', Vector3.new(15, 8.5, 8.5), CFrame.new(40.5, 7.5, 12) * CFrame.angles(0,0,math.rad(90)), Enum.Material.Limestone, st2, Enum.PartType.Cylinder)
-- gatehouse flanking turrets (square blocks)
add('CastleGateTurretL', Vector3.new(7, 15, 7), CFrame.new(-9.5, 7.5, -32.5), Enum.Material.Brick, Color3.fromRGB(212,196,180), Enum.PartType.Block)
add('CastleGateTurretR', Vector3.new(6.5, 14, 6.5), CFrame.new(9.5, 7, -32.5), Enum.Material.Brick, Color3.fromRGB(212,196,180), Enum.PartType.Block)
return 'B ok'

end)

run_chunk(3, function()

local c = {}
for _,d in pairs(workspace:GetDescendants()) do
  if d:IsA('Part') then table.insert(c, d.Name) end
end
return table.join_ and '' or table.concat(c, ',')

end)

run_chunk(4, function()

local W = workspace
local function add(n, s, c, m, col, sh)
  local p = Instance.new('Part')
  p.Name = n; p.Size = s; p.CFrame = c; p.Material = m
  if col then p.Color = col end
  p.Shape = sh; p.Anchored = true; p.Parent = W
end
local st = Color3.fromRGB(216, 210, 198)
local st2 = Color3.fromRGB(204, 198, 186)

add('CastleTowerNWDrum', Vector3.new(17, 10.5, 10.5), CFrame.new(-41, 8.5, -33.5) * CFrame.Angles(0,0,math.rad(90)), Enum.Material.Limestone, st, Enum.PartType.Cylinder)
add('CastleTowerNEDrum', Vector3.new(16, 10, 10), CFrame.new(41, 8, -33.5) * CFrame.Angles(0,0,math.rad(90)), Enum.Material.Limestone, st2, Enum.PartType.Cylinder)
add('CastleTowerSWDrum', Vector3.new(19, 11.5, 11.5), CFrame.new(-41, 9.5, 33.5) * CFrame.Angles(0,0,math.rad(90)), Enum.Material.Sandstone, Color3.fromRGB(226,216,196), Enum.PartType.Cylinder)
add('CastleTowerSEDrum', Vector3.new(15.5, 9.5, 9.5), CFrame.new(41, 7.75, 33.5) * CFrame.Angles(0,0,math.rad(90)), Enum.Material.Sandstone, Color3.fromRGB(226,216,196), Enum.PartType.Cylinder)
add('CastleTowerWDrum', Vector3.new(16.5, 9, 9), CFrame.new(-40.5, 8.25, -6) * CFrame.Angles(0,0,math.rad(90)), Enum.Material.Limestone, st, Enum.PartType.Cylinder)
add('CastleTowerEDrum', Vector3.new(15, 8.5, 8.5), CFrame.new(40.5, 7.5, 12) * CFrame.Angles(0,0,math.rad(90)), Enum.Material.Limestone, st2, Enum.PartType.Cylinder)
add('CastleGateTurretL', Vector3.new(7, 15, 7), CFrame.new(-9.5, 7.5, -32.5), Enum.Material.Brick, Color3.fromRGB(212,196,180), Enum.PartType.Block)
add('CastleGateTurretR', Vector3.new(6.5, 14, 6.5), CFrame.new(9.5, 7, -32.5), Enum.Material.Brick, Color3.fromRGB(212,196,180), Enum.PartType.Block)
return 'B ok'

end)

run_chunk(5, function()

local W = workspace
local function add(n, s, c, m, col, sh)
  local p = Instance.new('Part')
  p.Name = n; p.Size = s; p.CFrame = c; p.Material = m
  if col then p.Color = col end
  p.Shape = sh; p.Anchored = true; p.Parent = W
end
local keep1 = Color3.fromRGB(222,214,200)
local keep2 = Color3.fromRGB(206,198,184)

-- KEEP (free in courtyard, footprint 22 x 18)
add('CastleKeepWallMain', Vector3.new(22, 16, 18), CFrame.new(-5, 8, -4), Enum.Material.Limestone, keep1, Enum.PartType.Block)
add('CastleKeepUpperWall', Vector3.new(17, 6, 13), CFrame.new(-5, 19, -4), Enum.Material.Limestone, keep2, Enum.PartType.Block)
add('CastleKeepTurretDrum', Vector3.new(8, 22, 8), CFrame.new(-15.5, 11, -12.5) * CFrame.Angles(0,0,math.rad(90)), Enum.Material.Granite, Color3.fromRGB(214,208,198), Enum.PartType.Cylinder)
add('CastleKeepTurretRing', Vector3.new(10, 1.5, 10), CFrame.new(-15.5, 22.75, -12.5), Enum.Material.Slate, Color3.fromRGB(196,192,186), Enum.PartType.Block)
add('CastleKeepWallNorth', Vector3.new(22, 1.4, 1.4), CFrame.new(-5, 16.7, -12.3), Enum.Material.Granite, Color3.fromRGB(210,204,194), Enum.PartType.Block)
add('CastleKeepWallSouth', Vector3.new(22, 1.4, 1.4), CFrame.new(-5, 16.7, 4.3), Enum.Material.Granite, Color3.fromRGB(210,204,194), Enum.PartType.Block)
add('CastleKeepStair', Vector3.new(4, 1, 4), CFrame.new(7, 0.5, 6), Enum.Material.WoodPlanks, Color3.fromRGB(214,190,150), Enum.PartType.Block)
add('CastleKeepButtressA', Vector3.new(2.5, 12, 2.5), CFrame.new(-16.2, 6, 2), Enum.Material.Rock, Color3.fromRGB(206,200,188), Enum.PartType.Block)
add('CastleKeepButtressB', Vector3.new(2.5, 9, 2.5), CFrame.new(6.2, 4.5, -8), Enum.Material.Rock, Color3.fromRGB(206,200,188), Enum.PartType.Block)
add('CastleKeepDoor', Vector3.new(5, 7, 1.2), CFrame.new(-5, 3.5, 5.15), Enum.Material.Wood, Color3.fromRGB(206,170,124), Enum.PartType.Block)
add('CastleKeepWindowA', Vector3.new(2, 4, 1.2), CFrame.new(-12, 11, 5.15), Enum.Material.Glass, Color3.fromRGB(210,226,232), Enum.PartType.Block)
add('CastleKeepWindowB', Vector3.new(2, 4, 1.2), CFrame.new(4, 11, 5.15), Enum.Material.Glass, Color3.fromRGB(210,226,232), Enum.PartType.Block)
return 'C ok'

end)

run_chunk(6, function()

local W = workspace
local function add(n, s, c, m, col, sh)
  local p = Instance.new('Part')
  p.Name = n; p.Size = s; p.CFrame = c; p.Material = m
  if col then p.Color = col end
  p.Shape = sh; p.Anchored = true; p.Parent = W
end
local roofC = Color3.fromRGB(214,168,132)

-- Keep pyramid roof (4 corner wedges), base 18x14 at y=22, height 7
add('CastleKeepRoofA', Vector3.new(9, 7, 7), CFrame.new(-9.5, 25.5, -7.5) * CFrame.Angles(0,0,0), Enum.Material.ClayRoofTiles, roofC, Enum.PartType.CornerWedge)
add('CastleKeepRoofB', Vector3.new(9, 7, 7), CFrame.new(0.5, 25.5, -7.5) * CFrame.Angles(0,math.rad(90),0), Enum.Material.ClayRoofTiles, roofC, Enum.PartType.CornerWedge)
add('CastleKeepRoofC', Vector3.new(9, 7, 7), CFrame.new(0.5, 25.5, -0.5) * CFrame.Angles(0,math.rad(180),0), Enum.Material.ClayRoofTiles, roofC, Enum.PartType.CornerWedge)
add('CastleKeepRoofD', Vector3.new(9, 7, 7), CFrame.new(-9.5, 25.5, -0.5) * CFrame.Angles(0,math.rad(270),0), Enum.Material.ClayRoofTiles, roofC, Enum.PartType.CornerWedge)
add('CastleKeepFinial', Vector3.new(2.4, 2.4, 2.4), CFrame.new(-4.5, 30, -4), Enum.Material.Slate, Color3.fromRGB(120,120,130), Enum.PartType.Ball)
-- gate turret cone roofs (base 7x7 h=6) at y tops 15 and 14
add('CastleGateTurretRoofL', Vector3.new(7, 6, 7), CFrame.new(-9.5, 18, -32.5) * CFrame.Angles(0,math.rad(45),0), Enum.Material.RoofShingles, Color3.fromRGB(190,150,120), Enum.PartType.Cylinder)
return 'D ok'

end)

run_chunk(7, function()

local W = workspace
local function add(n, s, c, m, col, sh)
  local p = Instance.new('Part')
  p.Name = n; p.Size = s; p.CFrame = c; p.Material = m
  if col then p.Color = col end
  p.Shape = sh; p.Anchored = true; p.Parent = W
end
local function pyr(prefix, x, y, z, s, h, mat, col)
  local hs = s/2
  local q = s/2
  add(prefix..'A', Vector3.new(q, h, q), CFrame.new(x - hs + q/2, y + h/2, z - hs + q/2), mat, col, Enum.PartType.CornerWedge)
  add(prefix..'B', Vector3.new(q, h, q), CFrame.new(x + hs - q/2, y + h/2, z - hs + q/2) * CFrame.Angles(0,math.rad(90),0), mat, col, Enum.PartType.CornerWedge)
  add(prefix..'C', Vector3.new(q, h, q), CFrame.new(x + hs - q/2, y + h/2, z + hs - q/2) * CFrame.Angles(0,math.rad(180),0), mat, col, Enum.PartType.CornerWedge)
  add(prefix..'D', Vector3.new(q, h, q), CFrame.new(x - hs + q/2, y + h/2, z + hs - q/2) * CFrame.Angles(0,math.rad(270),0), mat, col, Enum.PartType.CornerWedge)
end
local rc = Color3.fromRGB(206,150,120)

pyr('CastleTowerNWRoof', -41, 17, -33.5, 12.5, 9, Enum.Material.ClayRoofTiles, rc)
return 'E ok'

end)

run_chunk(8, function()

local W = workspace
local function add(n, s, c, m, col, sh)
  local p = Instance.new('Part')
  p.Name = n; p.Size = s; p.CFrame = c; p.Material = m
  if col then p.Color = col end
  p.Shape = sh; p.Anchored = true; p.Parent = W
end
local function pyr(pfx, x, y, z, s, h, mat, col)
  local q = s/2
  add(pfx..'A', Vector3.new(q, h, q), CFrame.new(x - s/4, y + h/2, z - s/4), mat, col, Enum.PartType.CornerWedge)
  add(pfx..'B', Vector3.new(q, h, q), CFrame.new(x + s/4, y + h/2, z - s/4) * CFrame.Angles(0,math.rad(90),0), mat, col, Enum.PartType.CornerWedge)
  add(pfx..'C', Vector3.new(q, h, q), CFrame.new(x + s/4, y + h/2, z + s/4) * CFrame.Angles(0,math.rad(180),0), mat, col, Enum.PartType.CornerWedge)
  add(pfx..'D', Vector3.new(q, h, q), CFrame.new(x - s/4, y + h/2, z + s/4) * CFrame.Angles(0,math.rad(270),0), mat, col, Enum.PartType.CornerWedge)
end
local rc = Color3.fromRGB(212,158,126)
pyr('CastleTowerNERoof', 41, 16, -33.5, 12, 8.5, Enum.Material.ClayRoofTiles, rc)
pyr('CastleTowerSWRoof', -41, 15.25, 33.5, 13.5, 8, Enum.Material.RoofShingles, Color3.fromRGB(196,140,112))
pyr('CastleTowerSERoof', 41, 12.5, 33.5, 11.5, 7, Enum.Material.ClayRoofTiles, Color3.fromRGB(218,170,140))
add('CastleFinialNE', Vector3.new(2,2,2), CFrame.new(41, 25.6, -33.5), Enum.Material.Slate, Color3.fromRGB(150,150,160), Enum.PartType.Ball)
add('CastleFinialSW', Vector3.new(2.6,2.6,2.6), CFrame.new(-41, 24.2, 33.5), Enum.Material.Slate, Color3.fromRGB(150,150,160), Enum.PartType.Ball)
add('CastleFinialSE', Vector3.new(1.8,1.8,1.8), CFrame.new(41, 20.6, 33.5), Enum.Material.Slate, Color3.fromRGB(150,150,160), Enum.PartType.Ball)
return 'F ok'

end)

run_chunk(9, function()

local W = workspace
local function add(n, s, c, m, col, sh)
  local p = Instance.new('Part')
  p.Name = n; p.Size = s; p.CFrame = c; p.Material = m
  if col then p.Color = col end
  p.Shape = sh; p.Anchored = true; p.Parent = W
end
local function balldome(pfx, x, y, z, d, mat, col)
  add(pfx, Vector3.new(d,d,d), CFrame.new(x, y + d/2, z), mat, col, Enum.PartType.Ball)
end
-- mid tower W (drum top 12.75), E (drum top 16.75? check: size 8.5 center 7.5 -> top 11.75)
add('CastleTowerWRoof', Vector3.new(10.5, 9, 10.5), CFrame.new(-40.5, 17.25, -6) * CFrame.Angles(0,math.rad(45),0), Enum.Material.RoofShingles, Color3.fromRGB(186,132,108), Enum.PartType.Cylinder)
add('CastleTowerEWDrum2', Vector3.new(11, 4, 11), CFrame.new(40.5, 13.75, 12) * CFrame.Angles(0,0,math.rad(90)), Enum.Material.Granite, Color3.fromRGB(214,206,194), Enum.PartType.Cylinder)
add('CastleTowerERoof', Vector3.new(12.5, 8, 12.5), CFrame.new(40.5, 21.75, 12) * CFrame.Angles(0,math.rad(45),0), Enum.Material.ClayRoofTiles, Color3.fromRGB(216,166,132), Enum.PartType.Cylinder)
add('CastleTowerWFinial', Vector3.new(2.2,2.2,2.2), CFrame.new(-40.5, 22.4, -6), Enum.Material.Slate, Color3.fromRGB(150,150,160), Enum.PartType.Ball)
add('CastleTowerEFinial', Vector3.new(2.2,2.2,2.2), CFrame.new(40.5, 26.8, 12), Enum.Material.Slate, Color3.fromRGB(150,150,160), Enum.PartType.Ball)
-- crenellations on south wall (top y=11)
local i = 0
for x = -34, 34, 8 do
  if math.abs(x) > 7 then
    i = i + 1
    add('CastleCrenS'..i, Vector3.new(2.6, 2.2, 3), CFrame.new(x, 12.1, 32.5), Enum.Material.Granite, Color3.fromRGB(214,208,196), Enum.PartType.Block)
  end
end
return 'G ok'

end)

run_chunk(10, function()

local kill = {'CastleGateTurretRoofL','CastleTowerWRoof','CastleTowerERoof'}
local removed = 0
for _,n in ipairs(kill) do
  for _,d in pairs(workspace:GetDescendants()) do
    if d.Name == n then d:Remove(); removed = removed + 1 end
  end
end
local function add(n, s, c, m, col, sh)
  local p = Instance.new('Part')
  p.Name=n; p.Size=s; p.CFrame=c; p.Material=m; if col then p.Color=col end
  p.Shape=sh; p.Anchored=true; p.Parent=workspace
end
local rc = Color3.fromRGB(206,148,116)
-- gate turret pyramids (base 7x7 h 5.5) tops 15 and 14
add('CastleGateRoofL1', Vector3.new(3.5,5.5,3.5), CFrame.new(-11.25,17.75,-34.25), Enum.Material.RoofShingles, Color3.fromRGB(188,140,110), Enum.PartType.CornerWedge)
add('CastleGateRoofL2', Vector3.new(3.5,5.5,3.5), CFrame.new(-7.75,17.75,-34.25)*CFrame.Angles(0,math.rad(90),0), Enum.Material.RoofShingles, Color3.fromRGB(188,140,110), Enum.PartType.CornerWedge)
add('CastleGateRoofL3', Vector3.new(3.5,5.5,3.5), CFrame.new(-7.75,17.75,-30.75)*CFrame.Angles(0,math.rad(180),0), Enum.Material.RoofShingles, Color3.fromRGB(188,140,110), Enum.PartType.CornerWedge)
add('CastleGateRoofL4', Vector3.new(3.5,5.5,3.5), CFrame.new(-11.25,17.75,-30.75)*CFrame.Angles(0,math.rad(270),0), Enum.Material.RoofShingles, Color3.fromRGB(188,140,110), Enum.PartType.CornerWedge)
add('CastleGateRoofR1', Vector3.new(3.25,5,3.25), CFrame.new(11.125,16.5,-34.125), Enum.Material.RoofShingles, Color3.fromRGB(176,132,108), Enum.PartType.CornerWedge)
add('CastleGateRoofR2', Vector3.new(3.25,5,3.25), CFrame.new(7.875,16.5,-34.125)*CFrame.Angles(0,math.rad(90),0), Enum.Material.RoofShingles, Color3.fromRGB(176,132,108), Enum.PartType.CornerWedge)
add('CastleGateRoofR3', Vector3.new(3.25,5,3.25), CFrame.new(7.875,16.5,-30.875)*CFrame.Angles(0,math.rad(180),0), Enum.Material.RoofShingles, Color3.fromRGB(176,132,108), Enum.PartType.CornerWedge)
add('CastleGateRoofR4', Vector3.new(3.25,5,3.25), CFrame.new(11.125,16.5,-30.875)*CFrame.Angles(0,math.rad(270),0), Enum.Material.RoofShingles, Color3.fromRGB(176,132,108), Enum.PartType.CornerWedge)
-- ball domes on mid towers
add('CastleTowerWDome', Vector3.new(9.4,9.4,9.4), CFrame.new(-40.5, 17.45, -6), Enum.Material.Slate, Color3.fromRGB(120,128,140), Enum.PartType.Ball)
add('CastleTowerEDome', Vector3.new(11,11,11), CFrame.new(40.5, 19.75, 12), Enum.Material.Slate, Color3.fromRGB(116,124,138), Enum.PartType.Ball)
add('CastleGateFlagPoleL', Vector3.new(0.6,6,0.6), CFrame.new(-9.5,20.5,-32.5), Enum.Material.WoodPlanks, Color3.fromRGB(210,186,140), Enum.PartType.Block)
add('CastleGateBannerL', Vector3.new(4.5,3,0.3), CFrame.new(-7,21.5,-32.5), Enum.Material.Fabric, Color3.fromRGB(190,40,44), Enum.PartType.Block)
add('CastleGateFlagPoleR', Vector3.new(0.6,5,0.6), CFrame.new(9.5,19.5,-32.5), Enum.Material.WoodPlanks, Color3.fromRGB(210,186,140), Enum.PartType.Block)
add('CastleGateBannerR', Vector3.new(4,2.6,0.3), CFrame.new(11.7,20.4,-32.5), Enum.Material.Fabric, Color3.fromRGB(40,60,160), Enum.PartType.Block)
return 'H ok removed='..removed

end)

run_chunk(11, function()

local out = {}
for _,d in pairs(workspace:GetDescendants()) do
  if d:IsA('Part') then
    local n = d.Name
    if n:find('Wall') or n:find('Ground') or n:find('Gate') and (n:find('Lintel') or n:find('Arch')) then
      table.insert(out, n .. ' ' .. math.round(d.Position.X*10)/10 .. ',' .. math.round(d.Position.Y*10)/10 .. ',' .. math.round(d.Position.Z*10)/10 .. ' sz' .. math.round(d.Size.X*10)/10 .. ',' .. math.round(d.Size.Y*10)/10 .. ',' .. math.round(d.Size.Z*10)/10)
    end
  end
end
return table.concat(out, ' | ')

end)

run_chunk(12, function()

local out = {}
for _,d in pairs(workspace:GetDescendants()) do
  if d:IsA('Part') and (d.Name:find('Wall') or d.Name:find('Ground') or d.Name:find('Lintel') or d.Name:find('Arch')) then
    table.insert(out, d.Name .. ' p' .. string.format('%.1f',d.Position.X) .. ',' .. string.format('%.1f',d.Position.Y) .. ',' .. string.format('%.1f',d.Position.Z) .. ' s' .. string.format('%.1f',d.Size.X) .. ',' .. string.format('%.1f',d.Size.Y) .. ',' .. string.format('%.1f',d.Size.Z))
  end
end
return table.concat(out, ' | ')

end)

run_chunk(13, function()

local function add(n,s,c,m,col,sh) local p=Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m if col then p.Color=col end p.Shape=sh p.Anchored=true p.Parent=workspace end
add('CastleWellBase', Vector3.new(8,4,8), CFrame.new(20,2,16)*CFrame.Angles(0,0,math.rad(90)), Enum.Material.Cobblestone, Color3.fromRGB(200,192,178), Enum.PartType.Cylinder)
add('CastleWellPostA', Vector3.new(1,7,1), CFrame.new(16.5,3.5,16), Enum.Material.WoodPlanks, Color3.fromRGB(196,158,112), Enum.PartType.Block)
add('CastleWellPostB', Vector3.new(1,7,1), CFrame.new(23.5,3.5,16), Enum.Material.WoodPlanks, Color3.fromRGB(196,158,112), Enum.PartType.Block)
add('CastleWellBeam', Vector3.new(8,1,1.4), CFrame.new(20,7.5,16), Enum.Material.Wood, Color3.fromRGB(190,150,110), Enum.PartType.Block)
add('CastleWellRoof', Vector3.new(9,2,9), CFrame.new(20,8.5,16)*CFrame.Angles(0,math.rad(45),0), Enum.Material.RoofShingles, Color3.fromRGB(176,126,100), Enum.PartType.Cylinder)
add('CastleStallCanopy', Vector3.new(12,1,9), CFrame.new(-26,8,20)*CFrame.Angles(0,math.rad(9),0), Enum.Material.Fabric, Color3.fromRGB(214,178,60), Enum.PartType.Block)
add('CastleStallLegA', Vector3.new(1,8,1), CFrame.new(-31.5,4,24), Enum.Material.WoodPlanks, Color3.fromRGB(200,164,120), Enum.PartType.Block)
add('CastleStallLegB', Vector3.new(1,8,1), CFrame.new(-20.5,4,16.5), Enum.Material.WoodPlanks, Color3.fromRGB(200,164,120), Enum.PartType.Block)
add('CastleStallCounter', Vector3.new(10,3,3), CFrame.new(-26,1.5,20)*CFrame.Angles(0,math.rad(9),0), Enum.Material.WoodPlanks, Color3.fromRGB(208,174,130), Enum.PartType.Block)
add('CastleCrateA', Vector3.new(4,4,4), CFrame.new(-30,2,6), Enum.Material.Cardboard, Color3.fromRGB(214,190,150), Enum.PartType.Block)
add('CastleBarrelA', Vector3.new(5,4,4), CFrame.new(-24,2,8)*CFrame.Angles(0,0,math.rad(90)), Enum.Material.Wood, Color3.fromRGB(196,150,104), Enum.PartType.Cylinder)
add('CastleCampfire', Vector3.new(7,1,7), CFrame.new(8,0.5,20), Enum.Material.Mud, Color3.fromRGB(190,170,150), Enum.PartType.Block)
add('CastleFireGlow', Vector3.new(3.4,3.4,3.4), CFrame.new(8,2.2,20), Enum.Material.Neon, Color3.fromRGB(255,140,40), Enum.PartType.Ball)
add('CastleBench', Vector3.new(9,1,3), CFrame.new(-14,1.5,24)*CFrame.Angles(0,math.rad(-14),0), Enum.Material.WoodPlanks, Color3.fromRGB(212,178,128), Enum.PartType.Block)
add('CastleRoad', Vector3.new(14,0.6,30), CFrame.new(2,0.3,-50), Enum.Material.Pavement, Color3.fromRGB(196,192,186), Enum.PartType.Block)
return 'I ok'

end)

run_chunk(14, function()

local function add(n,s,c,m,col,sh) local p=Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m if col then p.Color=col end p.Shape=sh p.Anchored=true p.Parent=workspace end
add('CastleTent1', Vector3.new(9,8,9), CFrame.new(-52,4,-14)*CFrame.Angles(0,math.rad(45),0), Enum.Material.ClayRoofTiles, Color3.fromRGB(216,176,140), Enum.PartType.Cylinder)
add('CastleTent2', Vector3.new(8,7,8), CFrame.new(-50,3.5,22)*CFrame.Angles(0,math.rad(45),0), Enum.Material.Fabric, Color3.fromRGB(210,206,190), Enum.PartType.Cylinder)
add('CastleTreeTrunkA', Vector3.new(7,3.4,3.4), CFrame.new(54,3.5,-52)*CFrame.Angles(0,0,math.rad(90)), Enum.Material.WoodPlanks, Color3.fromRGB(186,146,104), Enum.PartType.Cylinder)
add('CastleTreeLeafA', Vector3.new(11,11,11), CFrame.new(54,10,-52), Enum.Material.LeafyGrass, Color3.fromRGB(190,214,170), Enum.PartType.Ball)
add('CastleTreeTrunkB', Vector3.new(8,3,3), CFrame.new(-30,4,-56)*CFrame.Angles(0,0,math.rad(90)), Enum.Material.WoodPlanks, Color3.fromRGB(186,146,104), Enum.PartType.Cylinder)
add('CastleTreeLeafB', Vector3.new(9.5,9.5,9.5), CFrame.new(-30,10.5,-56), Enum.Material.Grass, Color3.fromRGB(196,212,168), Enum.PartType.Ball)
add('CastleRockA', Vector3.new(7,5,6), CFrame.new(30,2.5,-54)*CFrame.Angles(0.2,0.4,0), Enum.Material.Basalt, Color3.fromRGB(196,192,188), Enum.PartType.Block)
add('CastleRockB', Vector3.new(4,3,4.5), CFrame.new(-56,1.5,-46), Enum.Material.Rock, Color3.fromRGB(204,198,186), Enum.PartType.Ball)
add('CastleOutbuilding', Vector3.new(12,9,10), CFrame.new(54,4.5,24), Enum.Material.Sandstone, Color3.fromRGB(222,206,178), Enum.PartType.Block)
add('CastleOutbuildRoofA', Vector3.new(12,5,5), CFrame.new(54,11.5,21.5)*CFrame.Angles(math.rad(-90),0,0), Enum.Material.ClayRoofTiles, Color3.fromRGB(214,160,126), Enum.PartType.Wedge)
add('CastleOutbuildRoofB', Vector3.new(12,5,5), CFrame.new(54,11.5,26.5)*CFrame.Angles(math.rad(90),0,0), Enum.Material.ClayRoofTiles, Color3.fromRGB(214,160,126), Enum.PartType.Wedge)
return 'J ok'

end)
