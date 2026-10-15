-- Model-authored execute_lua calls in original order.
-- Model: codex/GPT-6.1 Sol; scenario: g6_free_build_vision; calls: 15
-- pcall keeps later calls running after a failed section, matching the benchmark.
local function run_chunk(index, body)
    local ok, err = pcall(body)
    if not ok then print('G6 chunk ' .. index .. ' failed: ' .. tostring(err)) end
end

run_chunk(1, function()
local existing = workspace:GetDescendants()
report('Inspecting existing scene: '..#existing..' descendants')
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(232,229,220)
p('CastleGround',Vector3.new(128,0.5,128),CFrame.new(0,0.25,0),Enum.Material.LeafyGrass,Color3.fromRGB(218,234,204),Enum.PartType.Block)
p('CastleApproachRoad',Vector3.new(10,0.3,11),CFrame.new(0,0.65,-58.5),Enum.Material.Pavement,pale,Enum.PartType.Block)
p('CastleMoat',Vector3.new(118,0.6,11),CFrame.new(0,0.8,-47),Enum.Material.Glass,Color3.fromRGB(65,157,183),Enum.PartType.Block)
p('CastleDrawbridge',Vector3.new(10,1,21),CFrame.new(0,1.3,-43.5),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastleCurtainNorth',Vector3.new(84,14,3),CFrame.new(0,7.5,40),Enum.Material.Cobblestone,pale,Enum.PartType.Block)
p('CastleCurtainWest',Vector3.new(3,14,71),CFrame.new(-43,7.5,3),Enum.Material.Cobblestone,pale,Enum.PartType.Block)
p('CastleCurtainEast',Vector3.new(3,14,71),CFrame.new(43,7.5,3),Enum.Material.Cobblestone,pale,Enum.PartType.Block)
p('CastleCurtainSouthWest',Vector3.new(34,14,4),CFrame.new(-25,7.5,-34),Enum.Material.Cobblestone,pale,Enum.PartType.Block)
p('CastleCurtainSouthEast',Vector3.new(34,14,4),CFrame.new(25,7.5,-34),Enum.Material.Cobblestone,pale,Enum.PartType.Block)
for i,v in ipairs({{-42,-34},{42,-34},{-42,40},{42,40}}) do p('CastleRoundTower'..i,Vector3.new(26,14,14),CFrame.new(v[1],13.5,v[2])*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Granite,pale,Enum.PartType.Cylinder) end
p('CastleGatePierWest',Vector3.new(3,16,5),CFrame.new(-6.5,8.5,-34),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleGatePierEast',Vector3.new(3,16,5),CFrame.new(6.5,8.5,-34),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleGateLintel',Vector3.new(16,4,5.5),CFrame.new(0,15.5,-34),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleRoyalKeep',Vector3.new(22,28,18),CFrame.new(-8,14.5,20),Enum.Material.Brick,Color3.fromRGB(240,222,207),Enum.PartType.Block)
report('Main castle enclosure, four towers, open gate, bridge and freestanding keep built.')
end)

run_chunk(2, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(236,232,222)
local roof=Color3.fromRGB(211,222,236)
for i,v in ipairs({{-42,-34},{42,-34},{-42,40},{42,40}}) do
p('CastleTowerCornice'..i,Vector3.new(15,1,15),CFrame.new(v[1],27,v[2]),Enum.Material.Limestone,pale,Enum.PartType.Block)
for j,q in ipairs({{-3.75,-3.75,0},{3.75,-3.75,90},{3.75,3.75,180},{-3.75,3.75,270}}) do p('CastleTowerPyramid'..i..'Quadrant'..j,Vector3.new(7.5,9,7.5),CFrame.new(v[1]+q[1],32,v[2]+q[2])*CFrame.Angles(0,math.rad(q[3]),0),Enum.Material.Slate,roof,Enum.PartType.CornerWedge) end
end
report('Four slate pyramid roofs with limestone cornices completed.')
end)

run_chunk(3, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(235,231,222)
p('CastleKeepCornice',Vector3.new(23,1,19),CFrame.new(-8,28.5,20),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleKeepUpper',Vector3.new(20,8,16),CFrame.new(-8,33,20),Enum.Material.Plaster,Color3.fromRGB(246,236,214),Enum.PartType.Block)
p('CastleKeepRoofSouth',Vector3.new(24,10,10),CFrame.new(-8,42,15),Enum.Material.Slate,pale,Enum.PartType.Wedge)
p('CastleKeepRoofNorth',Vector3.new(24,10,10),CFrame.new(-8,42,25)*CFrame.Angles(0,math.rad(180),0),Enum.Material.Slate,pale,Enum.PartType.Wedge)
p('CastleKeepChimney',Vector3.new(2.5,13,2.5),CFrame.new(-13,43.5,23),Enum.Material.Brick,pale,Enum.PartType.Block)
p('CastleKeepChimneyCrown',Vector3.new(3.5,0.8,3.5),CFrame.new(-13,50.4,23),Enum.Material.Sandstone,pale,Enum.PartType.Block)
p('CastleKeepDoor',Vector3.new(4.5,7,0.6),CFrame.new(-8,4,10.8),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
for i,x in ipairs({-15,-1}) do
p('CastleKeepLowerWindow'..i,Vector3.new(2,4,0.45),CFrame.new(x,15,10.85),Enum.Material.Glass,Color3.fromRGB(97,165,201),Enum.PartType.Block)
p('CastleKeepHighWindow'..i,Vector3.new(2,4,0.5),CFrame.new(x,24,10.8),Enum.Material.Glass,Color3.fromRGB(224,176,76),Enum.PartType.Block)
end
p('CastleGreatHall',Vector3.new(18,12,18),CFrame.new(23,6.5,16),Enum.Material.Sandstone,pale,Enum.PartType.Block)
p('CastleGreatHallRoofSouth',Vector3.new(20,7,10),CFrame.new(23,16,11),Enum.Material.ClayRoofTiles,pale,Enum.PartType.Wedge)
p('CastleGreatHallRoofNorth',Vector3.new(20,7,10),CFrame.new(23,16,21)*CFrame.Angles(0,math.rad(180),0),Enum.Material.ClayRoofTiles,pale,Enum.PartType.Wedge)
p('CastleSmithy',Vector3.new(14,9,12),CFrame.new(-25,5,-9),Enum.Material.Plaster,pale,Enum.PartType.Block)
p('CastleSmithyRoofSouth',Vector3.new(16,6,7),CFrame.new(-25,12.5,-12.5),Enum.Material.RoofShingles,pale,Enum.PartType.Wedge)
p('CastleSmithyRoofNorth',Vector3.new(16,6,7),CFrame.new(-25,12.5,-5.5)*CFrame.Angles(0,math.rad(180),0),Enum.Material.RoofShingles,pale,Enum.PartType.Wedge)
p('CastleKeepButtressWest',Vector3.new(3,12,7),CFrame.new(-20.5,6.5,20)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Limestone,pale,Enum.PartType.Wedge)
p('CastleKeepButtressEast',Vector3.new(3,12,7),CFrame.new(4.5,6.5,20)*CFrame.Angles(0,math.rad(-90),0),Enum.Material.Limestone,pale,Enum.PartType.Wedge)
p('CastleKeepCrownFinial',Vector3.new(2,2,2),CFrame.new(-13,51.8,23),Enum.Material.Metal,Color3.fromRGB(242,222,181),Enum.PartType.Ball)
report('Layered keep, great hall and smithy completed with distinct roofs and windows.')
end)

run_chunk(4, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(235,232,223)
for i,x in ipairs({-30,-18,-6,6,18,30}) do p('CastleNorthMerlon'..i,Vector3.new(3.5,3,3),CFrame.new(x,16,40),Enum.Material.Limestone,pale,Enum.PartType.Block) end
for i,z in ipairs({-22,-10,2,14,26}) do
p('CastleWestMerlon'..i,Vector3.new(3,3,3.5),CFrame.new(-43,16,z),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleEastMerlon'..i,Vector3.new(3,3,3.5),CFrame.new(43,16,z),Enum.Material.Limestone,pale,Enum.PartType.Block)
end
p('CastleSouthMerlonWest',Vector3.new(4,3,4),CFrame.new(-20,16,-34),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleSouthMerlonEast',Vector3.new(4,3,4),CFrame.new(20,16,-34),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleGateCrownWest',Vector3.new(3,3,5.5),CFrame.new(-6,19,-34),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleGateCrownEast',Vector3.new(3,3,5.5),CFrame.new(6,19,-34),Enum.Material.Limestone,pale,Enum.PartType.Block)
report('Twenty crenellations completed around the curtain and gatehouse.')
end)

run_chunk(5, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(231,230,221)
for i,v in ipairs({{-42,-34},{42,-34},{-42,40},{42,40}}) do p('CastleTowerArrowWindow'..i,Vector3.new(0.8,5,0.45),CFrame.new(v[1],19,v[2]-6.95),Enum.Material.Glass,Color3.fromRGB(75,130,164),Enum.PartType.Block) end
for i,x in ipairs({-4,-2,0,2,4}) do p('CastlePortcullisBar'..i,Vector3.new(13,0.22,0.22),CFrame.new(x,7.5,-34)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Metal,pale,Enum.PartType.Cylinder) end
p('CastlePortcullisCrossbarLow',Vector3.new(9,0.3,0.3),CFrame.new(0,4,-34),Enum.Material.CorrodedMetal,pale,Enum.PartType.Block)
p('CastlePortcullisCrossbarHigh',Vector3.new(9,0.3,0.3),CFrame.new(0,11,-34),Enum.Material.CorrodedMetal,pale,Enum.PartType.Block)
p('CastleRoyalFlagpole',Vector3.new(8,0.35,0.35),CFrame.new(-6.5,24.5,-34)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Metal,pale,Enum.PartType.Cylinder)
p('CastleRoyalFlag',Vector3.new(5,3,0.15),CFrame.new(-3.825,27,-34),Enum.Material.Fabric,Color3.fromRGB(169,25,53),Enum.PartType.Block)
p('CastleGateBraceWest',Vector3.new(4,5,7),CFrame.new(-10,3,-37),Enum.Material.Rock,pale,Enum.PartType.CornerWedge)
p('CastleGateBraceEast',Vector3.new(4,5,7),CFrame.new(10,3,-37)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Rock,pale,Enum.PartType.CornerWedge)
p('CastleKeepBalcony',Vector3.new(9,0.8,3.5),CFrame.new(-8,19,9.75),Enum.Material.Marble,pale,Enum.PartType.Block)
p('CastleKeepBalconyBracketWest',Vector3.new(1,3,2),CFrame.new(-11,17.1,10),Enum.Material.Limestone,pale,Enum.PartType.Wedge)
p('CastleKeepBalconyBracketEast',Vector3.new(1,3,2),CFrame.new(-5,17.1,10),Enum.Material.Limestone,pale,Enum.PartType.Wedge)
p('CastleGateLampWest',Vector3.new(1.2,1.2,1.2),CFrame.new(-6.5,10,-36.8),Enum.Material.Neon,Color3.fromRGB(255,191,85),Enum.PartType.Ball)
p('CastleGateLampEast',Vector3.new(1.2,1.2,1.2),CFrame.new(6.5,10,-36.8),Enum.Material.Neon,Color3.fromRGB(255,191,85),Enum.PartType.Ball)
report('Portcullis, royal standard, arrow windows, balcony and gate lamps completed.')
end)

run_chunk(6, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(236,230,216)
local vertical=CFrame.Angles(0,0,math.rad(90))
p('CastleWellBase',Vector3.new(2,6,6),CFrame.new(11,1.5,-7)*vertical,Enum.Material.Sandstone,pale,Enum.PartType.Cylinder)
p('CastleWellRim',Vector3.new(0.7,7,7),CFrame.new(11,2.85,-7)*vertical,Enum.Material.Limestone,pale,Enum.PartType.Cylinder)
p('CastleWellWater',Vector3.new(0.1,4,4),CFrame.new(11,3.25,-7)*vertical,Enum.Material.Glass,Color3.fromRGB(64,145,179),Enum.PartType.Cylinder)
p('CastleWellPostWest',Vector3.new(0.6,7,0.6),CFrame.new(8,5,-7),Enum.Material.Wood,pale,Enum.PartType.Block)
p('CastleWellPostEast',Vector3.new(0.6,7,0.6),CFrame.new(14,5,-7),Enum.Material.Wood,pale,Enum.PartType.Block)
p('CastleWellRoofSouth',Vector3.new(10,3,3),CFrame.new(11,10,-8.5),Enum.Material.ClayRoofTiles,pale,Enum.PartType.Wedge)
p('CastleWellRoofNorth',Vector3.new(10,3,3),CFrame.new(11,10,-5.5)*CFrame.Angles(0,math.rad(180),0),Enum.Material.ClayRoofTiles,pale,Enum.PartType.Wedge)
for i,v in ipairs({{19,-16},{29,-16},{19,-10},{29,-10}}) do p('CastleMarketPost'..i,Vector3.new(0.6,6,0.6),CFrame.new(v[1],3.5,v[2]),Enum.Material.Wood,pale,Enum.PartType.Block) end
p('CastleMarketCanopySouth',Vector3.new(12,2,4),CFrame.new(24,7.5,-15),Enum.Material.Fabric,Color3.fromRGB(178,36,51),Enum.PartType.Wedge)
p('CastleMarketCanopyNorth',Vector3.new(12,2,4),CFrame.new(24,7.5,-11)*CFrame.Angles(0,math.rad(180),0),Enum.Material.Fabric,Color3.fromRGB(247,220,165),Enum.PartType.Wedge)
p('CastleMarketCounter',Vector3.new(12,2,2),CFrame.new(24,2,-15),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastleSupplyCrateSmall',Vector3.new(4,3,4),CFrame.new(-24,2,-20)*CFrame.Angles(0,math.rad(9),0),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastleSupplyCrateLarge',Vector3.new(4,4,4),CFrame.new(-29,2.5,-19)*CFrame.Angles(0,math.rad(-12),0),Enum.Material.Wood,pale,Enum.PartType.Block)
p('CastleSupplyBarrel',Vector3.new(4,3,3),CFrame.new(-19,2.5,-22)*vertical,Enum.Material.Wood,pale,Enum.PartType.Cylinder)
p('CastleCourtyardBenchSeat',Vector3.new(8,0.7,2),CFrame.new(-2,2.5,-14),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastleCourtyardBenchLegWest',Vector3.new(0.7,1.65,1.6),CFrame.new(-5,1.325,-14),Enum.Material.Metal,pale,Enum.PartType.Block)
p('CastleCourtyardBenchLegEast',Vector3.new(0.7,1.65,1.6),CFrame.new(1,1.325,-14),Enum.Material.Metal,pale,Enum.PartType.Block)
report('Courtyard well, covered market, supplies and seating completed.')
end)

run_chunk(7, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(234,229,217)
local vertical=CFrame.Angles(0,0,math.rad(90))
p('CastleRearStable',Vector3.new(16,8,10),CFrame.new(20,4.5,50),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastleRearStableRoofSouth',Vector3.new(18,5,6),CFrame.new(20,11,47),Enum.Material.RoofShingles,pale,Enum.PartType.Wedge)
p('CastleRearStableRoofNorth',Vector3.new(18,5,6),CFrame.new(20,11,53)*CFrame.Angles(0,math.rad(180),0),Enum.Material.RoofShingles,pale,Enum.PartType.Wedge)
p('CastleRearCottage',Vector3.new(12,7,9),CFrame.new(-20,4,51),Enum.Material.Plaster,pale,Enum.PartType.Block)
p('CastleRearCottageRoofSouth',Vector3.new(14,5,5.5),CFrame.new(-20,10,48.25),Enum.Material.ClayRoofTiles,pale,Enum.PartType.Wedge)
p('CastleRearCottageRoofNorth',Vector3.new(14,5,5.5),CFrame.new(-20,10,53.75)*CFrame.Angles(0,math.rad(180),0),Enum.Material.ClayRoofTiles,pale,Enum.PartType.Wedge)
p('CastleWestTreeTrunk',Vector3.new(6,2.5,2.5),CFrame.new(-54,3.5,-16)*vertical,Enum.Material.Wood,pale,Enum.PartType.Cylinder)
p('CastleWestTreeCrown',Vector3.new(10,10,10),CFrame.new(-54,11,-16),Enum.Material.Grass,Color3.fromRGB(202,230,187),Enum.PartType.Ball)
p('CastleEastTreeTrunk',Vector3.new(8,3,3),CFrame.new(54,4.5,17)*vertical,Enum.Material.Wood,pale,Enum.PartType.Cylinder)
p('CastleEastTreeCrown',Vector3.new(12,12,12),CFrame.new(54,14,17),Enum.Material.LeafyGrass,Color3.fromRGB(209,234,191),Enum.PartType.Ball)
p('CastleWestGardenHedge',Vector3.new(3,5,20),CFrame.new(-54,3,20),Enum.Material.LeafyGrass,Color3.fromRGB(196,222,184),Enum.PartType.Block)
p('CastleNorthGardenHedge',Vector3.new(3,4,9),CFrame.new(-55,2.5,40),Enum.Material.LeafyGrass,Color3.fromRGB(210,231,188),Enum.PartType.Block)
p('CastleEastBoulder',Vector3.new(6,6,6),CFrame.new(54,3.5,-26),Enum.Material.Rock,pale,Enum.PartType.Ball)
p('CastleSouthWestBoulder',Vector3.new(5,5,5),CFrame.new(-55,3,-57),Enum.Material.Basalt,pale,Enum.PartType.Ball)
for i,x in ipairs({-22,22}) do
p('CastleApproachBenchSeat'..i,Vector3.new(8,0.7,2),CFrame.new(x,2.5,-58),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastleApproachBenchLegA'..i,Vector3.new(0.7,1.65,1.6),CFrame.new(x-3,1.325,-58),Enum.Material.CorrodedMetal,pale,Enum.PartType.Block)
p('CastleApproachBenchLegB'..i,Vector3.new(0.7,1.65,1.6),CFrame.new(x+3,1.325,-58),Enum.Material.CorrodedMetal,pale,Enum.PartType.Block)
end
report('Rear village, gardens, trees, boulders and approach seating completed.')
end)

run_chunk(8, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(237,233,223)
p('CastleMoatBankSouth',Vector3.new(118,0.8,1),CFrame.new(0,0.9,-53),Enum.Material.Pebble,pale,Enum.PartType.Block)
p('CastleMoatBankNorth',Vector3.new(118,0.8,1),CFrame.new(0,0.9,-41),Enum.Material.Pebble,pale,Enum.PartType.Block)
p('CastleCourtyardMainPath',Vector3.new(8,0.15,44),CFrame.new(0,0.575,-11),Enum.Material.Pavement,pale,Enum.PartType.Block)
p('CastleHallPath',Vector3.new(24,0.2,6),CFrame.new(13,0.6,6),Enum.Material.Pebble,pale,Enum.PartType.Block)
for i,v in ipairs({{-45,-16,90},{-45,19,90},{45,-15,-90},{45,29,-90}}) do p('CastleCurtainButtress'..i,Vector3.new(7,8,4),CFrame.new(v[1],4.5,v[2])*CFrame.Angles(0,math.rad(v[3]),0),Enum.Material.Rock,pale,Enum.PartType.Wedge) end
for i,v in ipairs({{-20,59},{-20,62},{20,59},{20,62}}) do p('CastleVillageSteppingStone'..i,Vector3.new(3,0.25,2),CFrame.new(v[1],0.625,v[2])*CFrame.Angles(0,math.rad(i*7),0),Enum.Material.Pebble,pale,Enum.PartType.Block) end
p('CastleHallWindowWest',Vector3.new(2,5,0.5),CFrame.new(18,8,6.85),Enum.Material.Glass,Color3.fromRGB(92,159,194),Enum.PartType.Block)
p('CastleHallWindowEast',Vector3.new(2,5,0.5),CFrame.new(28,8,6.85),Enum.Material.Glass,Color3.fromRGB(92,159,194),Enum.PartType.Block)
p('CastleStableDoor',Vector3.new(5,6,0.5),CFrame.new(20,3.5,44.85),Enum.Material.Wood,pale,Enum.PartType.Block)
p('CastleGardenFlowerRose',Vector3.new(2,2,2),CFrame.new(-57,1.5,13),Enum.Material.SmoothPlastic,Color3.fromRGB(209,66,103),Enum.PartType.Ball)
p('CastleGardenFlowerGold',Vector3.new(2,2,2),CFrame.new(-57,1.5,20),Enum.Material.SmoothPlastic,Color3.fromRGB(246,195,71),Enum.PartType.Ball)
p('CastleGardenFlowerLavender',Vector3.new(2,2,2),CFrame.new(-57,1.5,27),Enum.Material.SmoothPlastic,Color3.fromRGB(151,105,205),Enum.PartType.Ball)
report('Castle construction complete: surfaced paths, moat banks, buttresses and landscape details added.')
end)

run_chunk(9, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(237,233,224)
local vertical=CFrame.Angles(0,0,math.rad(90))
p('CastleRoyalWatchSpire',Vector3.new(26,8,8),CFrame.new(-8,50,20)*vertical,Enum.Material.Limestone,pale,Enum.PartType.Cylinder)
p('CastleRoyalSpireCornice',Vector3.new(1,10,10),CFrame.new(-8,63.5,20)*vertical,Enum.Material.Marble,pale,Enum.PartType.Cylinder)
p('CastleRoyalSpireDome',Vector3.new(10,10,10),CFrame.new(-8,69,20),Enum.Material.Metal,Color3.fromRGB(222,235,224),Enum.PartType.Ball)
p('CastleRoyalSpireFlagpole',Vector3.new(8,0.3,0.3),CFrame.new(-8,77,20)*vertical,Enum.Material.Metal,pale,Enum.PartType.Cylinder)
p('CastleRoyalSpireStandard',Vector3.new(6,3,0.15),CFrame.new(-4.85,79,20),Enum.Material.Fabric,Color3.fromRGB(166,24,49),Enum.PartType.Block)
p('CastleRoyalSpirePoleFinial',Vector3.new(0.8,0.8,0.8),CFrame.new(-8,81.3,20),Enum.Material.Metal,Color3.fromRGB(248,228,184),Enum.PartType.Ball)
p('CastleRoyalSpireSouthWindow',Vector3.new(1.2,5,0.4),CFrame.new(-8,57,16),Enum.Material.Glass,Color3.fromRGB(74,149,185),Enum.PartType.Block)
p('CastleRoyalSpireEastWindow',Vector3.new(0.4,5,1.2),CFrame.new(-4,57,20),Enum.Material.Glass,Color3.fromRGB(74,149,185),Enum.PartType.Block)
p('CastleCourtyardChapel',Vector3.new(10,17,18),CFrame.new(-29,9,20),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleChapelRoofSouth',Vector3.new(12,7,10),CFrame.new(-29,21,15),Enum.Material.Slate,pale,Enum.PartType.Wedge)
p('CastleChapelRoofNorth',Vector3.new(12,7,10),CFrame.new(-29,21,25)*CFrame.Angles(0,math.rad(180),0),Enum.Material.Slate,pale,Enum.PartType.Wedge)
p('CastleChapelOakDoor',Vector3.new(3,6,0.5),CFrame.new(-29,3.5,10.85),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastleChapelRoseWindow',Vector3.new(0.35,3.5,3.5),CFrame.new(-29,13,10.85)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Glass,Color3.fromRGB(80,128,214),Enum.PartType.Cylinder)
p('CastleChapelEastWindow',Vector3.new(0.4,7,2),CFrame.new(-23.9,11,20),Enum.Material.Glass,Color3.fromRGB(225,156,62),Enum.PartType.Block)
p('CastleChapelBellTower',Vector3.new(27,6,6),CFrame.new(-29,14,29)*vertical,Enum.Material.Sandstone,pale,Enum.PartType.Cylinder)
p('CastleChapelBellCornice',Vector3.new(1,8,8),CFrame.new(-29,28,29)*vertical,Enum.Material.Limestone,pale,Enum.PartType.Cylinder)
p('CastleChapelBellDome',Vector3.new(8,8,8),CFrame.new(-29,32.5,29),Enum.Material.Slate,pale,Enum.PartType.Ball)
p('CastleChapelBellWindow',Vector3.new(0.4,4,1.5),CFrame.new(-25.9,24,29),Enum.Material.Glass,Color3.fromRGB(93,148,176),Enum.PartType.Block)
p('CastleFrontMerlonWestOuter',Vector3.new(4,3,4),CFrame.new(-30,16,-34),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleFrontMerlonEastOuter',Vector3.new(4,3,4),CFrame.new(30,16,-34),Enum.Material.Limestone,pale,Enum.PartType.Block)
report('Added a crowned royal watch spire, courtyard chapel with bell tower, and additional gateward battlements.')
end)

run_chunk(10, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(237,232,221)
for i,x in ipairs({-4.65,4.65}) do
for j,z in ipairs({-51,-44,-37}) do p('CastleBridgeRailingPost'..i..'_'..j,Vector3.new(0.5,3,0.5),CFrame.new(x,3.3,z),Enum.Material.Wood,pale,Enum.PartType.Block) end
p('CastleBridgeHandrail'..i,Vector3.new(0.55,0.5,15),CFrame.new(x,4.8,-44),Enum.Material.Wood,pale,Enum.PartType.Block)
p('CastleDrawbridgeLiftingChain'..i,Vector3.new(20.3,0.24,0.24),CFrame.new(x,9.25,-43.9)*CFrame.Angles(math.rad(-45.6),math.rad(-90),0),Enum.Material.CorrodedMetal,pale,Enum.PartType.Cylinder)
end
p('CastleKeepCeremonialBanner',Vector3.new(3,8,0.18),CFrame.new(-8,11,10.75),Enum.Material.Fabric,Color3.fromRGB(164,27,49),Enum.PartType.Block)
p('CastleKeepBannerEmblem',Vector3.new(1.2,1.2,0.15),CFrame.new(-8,12,10.585)*CFrame.Angles(0,0,math.rad(45)),Enum.Material.SmoothPlastic,Color3.fromRGB(249,206,79),Enum.PartType.Block)
p('CastleHallDoor',Vector3.new(3.5,7,0.6),CFrame.new(23,4,6.8),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastleSmithyChimney',Vector3.new(2.5,11,2.5),CFrame.new(-29,15,-7),Enum.Material.Brick,pale,Enum.PartType.Block)
p('CastleSmithyChimneyCap',Vector3.new(3.4,0.7,3.4),CFrame.new(-29,20.85,-7),Enum.Material.Basalt,pale,Enum.PartType.Block)
p('CastleSmithyDoor',Vector3.new(3,6,0.5),CFrame.new(-25,3.5,-15.1),Enum.Material.Wood,pale,Enum.PartType.Block)
p('CastleRearCottageDoor',Vector3.new(3,5.5,0.5),CFrame.new(-20,3.25,46.35),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastleRearCottageWindow',Vector3.new(2,2.5,0.4),CFrame.new(-23.5,4.5,46.4),Enum.Material.Glass,Color3.fromRGB(240,183,88),Enum.PartType.Block)
p('CastleRearCottageChimney',Vector3.new(2,7,2),CFrame.new(-23,12,52),Enum.Material.Brick,pale,Enum.PartType.Block)
p('CastleRearCottageChimneyCap',Vector3.new(2.8,0.6,2.8),CFrame.new(-23,15.8,52),Enum.Material.Limestone,pale,Enum.PartType.Block)
report('Added supported drawbridge rails and lifting chains, royal heraldry, doors, and village chimneys.')
end)

run_chunk(11, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(239,235,225)
p('CastleNorthWallWalk',Vector3.new(72,0.8,3.5),CFrame.new(0,13.6,36.75),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleWestWallWalk',Vector3.new(3.5,0.8,60),CFrame.new(-39.75,13.6,3),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleEastWallWalk',Vector3.new(3.5,0.8,60),CFrame.new(39.75,13.6,3),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleSouthWestWallWalk',Vector3.new(30,0.8,3.5),CFrame.new(-23,13.6,-30.25),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleSouthEastWallWalk',Vector3.new(30,0.8,3.5),CFrame.new(23,13.6,-30.25),Enum.Material.Limestone,pale,Enum.PartType.Block)
p('CastleNorthAccessRamp',Vector3.new(4.5,13.5,15),CFrame.new(8.5,7.25,27.5),Enum.Material.Cobblestone,pale,Enum.PartType.Wedge)
p('CastleWestAccessRamp',Vector3.new(4.5,13.5,15),CFrame.new(-30.5,7.25,-27)*CFrame.Angles(0,math.rad(-90),0),Enum.Material.Cobblestone,pale,Enum.PartType.Wedge)
p('CastleEastAccessRamp',Vector3.new(4.5,13.5,15),CFrame.new(30.5,7.25,-25)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Cobblestone,pale,Enum.PartType.Wedge)
for i,v in ipairs({{-42,-34},{42,-34},{-42,40},{42,40}}) do p('CastleTowerUpperStringcourse'..i,Vector3.new(0.8,14.6,14.6),CFrame.new(v[1],23,v[2])*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Limestone,pale,Enum.PartType.Cylinder) end
for i,x in ipairs({-22,22}) do
p('CastleCurtainHeraldicBanner'..i,Vector3.new(3,6,0.2),CFrame.new(x,10,-36.08),Enum.Material.Fabric,Color3.fromRGB(167,28,54),Enum.PartType.Block)
p('CastleCurtainGoldenEmblem'..i,Vector3.new(1.1,1.1,0.15),CFrame.new(x,10.5,-36.235)*CFrame.Angles(0,0,math.rad(45)),Enum.Material.SmoothPlastic,Color3.fromRGB(249,207,85),Enum.PartType.Block)
end
p('CastleGateArchShoulderWest',Vector3.new(4.5,3,3),CFrame.new(-3.5,12,-34)*CFrame.Angles(0,math.rad(-90),0),Enum.Material.Limestone,pale,Enum.PartType.Wedge)
p('CastleGateArchShoulderEast',Vector3.new(4.5,3,3),CFrame.new(3.5,12,-34)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Limestone,pale,Enum.PartType.Wedge)
report('Added five supported wall walks, three access ramps, tower stringcourses, heraldic banners and shaped gate arch shoulders.')
end)

run_chunk(12, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(237,232,221)
p('CastleWestCampaignTentSouth',Vector3.new(12,6,5),CFrame.new(-55,3.5,-2.5),Enum.Material.Fabric,Color3.fromRGB(224,211,178),Enum.PartType.Wedge)
p('CastleWestCampaignTentNorth',Vector3.new(12,6,5),CFrame.new(-55,3.5,2.5)*CFrame.Angles(0,math.rad(180),0),Enum.Material.Fabric,Color3.fromRGB(47,89,151),Enum.PartType.Wedge)
p('CastleCampaignTentRidge',Vector3.new(12.5,0.35,0.35),CFrame.new(-55,6.5,0),Enum.Material.Wood,pale,Enum.PartType.Cylinder)
p('CastleRearOrchardTrunk',Vector3.new(7,2,2),CFrame.new(0,4,55)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,pale,Enum.PartType.Cylinder)
p('CastleRearOrchardCrown',Vector3.new(9,9,9),CFrame.new(0,11.5,55),Enum.Material.LeafyGrass,Color3.fromRGB(207,230,186),Enum.PartType.Ball)
p('CastleRearOrchardSideCrown',Vector3.new(6,6,6),CFrame.new(4,10,56),Enum.Material.LeafyGrass,Color3.fromRGB(221,236,193),Enum.PartType.Ball)
p('CastleProvisionCartBed',Vector3.new(6,0.7,8),CFrame.new(55,4.4,-5),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
for i,v in ipairs({{52.5,-7.5},{57.5,-7.5},{52.5,-2.5},{57.5,-2.5}}) do p('CastleProvisionCartWheel'..i,Vector3.new(0.7,4,4),CFrame.new(v[1],2.5,v[2]),Enum.Material.Wood,pale,Enum.PartType.Cylinder) end
p('CastleCartCargoCrate',Vector3.new(3,3,3),CFrame.new(54,6.25,-6.5),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastleCartCargoBarrel',Vector3.new(3,2.5,2.5),CFrame.new(56,6.25,-3.5)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,pale,Enum.PartType.Cylinder)
p('CastleTrainingTargetFoot',Vector3.new(5,0.5,3),CFrame.new(24,0.75,-25),Enum.Material.Wood,pale,Enum.PartType.Block)
p('CastleTrainingTargetPost',Vector3.new(0.6,5.5,0.6),CFrame.new(24,3.75,-25),Enum.Material.Wood,pale,Enum.PartType.Block)
p('CastleTrainingTargetBoard',Vector3.new(0.5,4,4),CFrame.new(24,5,-25.45)*CFrame.Angles(0,math.rad(90),0),Enum.Material.WoodPlanks,pale,Enum.PartType.Cylinder)
p('CastleTrainingTargetOuterMark',Vector3.new(0.12,2.6,2.6),CFrame.new(24,5,-25.75)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Fabric,Color3.fromRGB(178,37,48),Enum.PartType.Cylinder)
p('CastleTrainingTargetBullseye',Vector3.new(0.1,1,1),CFrame.new(24,5,-25.85)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Fabric,Color3.fromRGB(243,218,163),Enum.PartType.Cylinder)
report('Added a campaign tent, rear orchard tree, loaded provision cart and courtyard archery target: eighteen supported details.')
end)

run_chunk(13, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(237,233,224)
local blue=Color3.fromRGB(100,166,198)
for i,x in ipairs({-11.8,-4.2}) do
p('CastleBalconyRailingPost'..i,Vector3.new(0.4,2.8,0.4),CFrame.new(x,20.8,8.2),Enum.Material.Metal,pale,Enum.PartType.Block)
p('CastleBalconyRailingFinial'..i,Vector3.new(0.7,0.7,0.7),CFrame.new(x,22.55,8.2),Enum.Material.Metal,pale,Enum.PartType.Ball)
end
p('CastleBalconyFrontHandrail',Vector3.new(8,0.35,0.4),CFrame.new(-8,22.2,8.2),Enum.Material.Metal,pale,Enum.PartType.Block)
for i,z in ipairs({15,25}) do
p('CastleKeepEastMiddleWindow'..i,Vector3.new(0.4,4,2),CFrame.new(3.15,14,z),Enum.Material.Glass,blue,Enum.PartType.Block)
p('CastleKeepEastUpperWindow'..i,Vector3.new(0.4,4,2),CFrame.new(3.15,24,z),Enum.Material.Glass,blue,Enum.PartType.Block)
end
for i,x in ipairs({-14,-2}) do
p('CastleKeepRearMiddleWindow'..i,Vector3.new(2,4,0.4),CFrame.new(x,14,29.15),Enum.Material.Glass,blue,Enum.PartType.Block)
p('CastleKeepRearUpperWindow'..i,Vector3.new(2,4,0.4),CFrame.new(x,24,29.15),Enum.Material.Glass,blue,Enum.PartType.Block)
end
for i,z in ipairs({12,20}) do p('CastleHallEastWindow'..i,Vector3.new(0.4,5,2),CFrame.new(32.15,8,z),Enum.Material.Glass,blue,Enum.PartType.Block) end
for i,x in ipairs({-9,9}) do
p('CastleApproachLanternColumn'..i,Vector3.new(6,0.65,0.65),CFrame.new(x,3.5,-58)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Metal,pale,Enum.PartType.Cylinder)
p('CastleApproachLanternGlobe'..i,Vector3.new(1.6,1.6,1.6),CFrame.new(x,7.2,-58),Enum.Material.Neon,Color3.fromRGB(255,207,125),Enum.PartType.Ball)
end
report('Added nineteen details: balcony railing and finials, keep and hall windows, and two supported approach lanterns.')
end)

run_chunk(14, function()
local function p(n,s,c,m,col,sh) local a=Instance.new('Part'); a.Name=n; a.Size=s; a.CFrame=c; a.Material=m; a.Color=col; a.Shape=sh; a.Anchored=true; a.Parent=workspace end
local pale=Color3.fromRGB(237,232,219)
for i,v in ipairs({{35,51},{46,51},{35,61},{46,61}}) do p('CastleStablePaddockPost'..i,Vector3.new(0.7,3.5,0.7),CFrame.new(v[1],2.25,v[2]),Enum.Material.Wood,pale,Enum.PartType.Block) end
for i,y in ipairs({1.6,3.2}) do
p('CastlePaddockSouthRail'..i,Vector3.new(10.3,0.4,0.45),CFrame.new(40.5,y,51),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastlePaddockNorthRail'..i,Vector3.new(10.3,0.4,0.45),CFrame.new(40.5,y,61),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastlePaddockWestRail'..i,Vector3.new(0.45,0.4,9.3),CFrame.new(35,y,56),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
p('CastlePaddockEastRail'..i,Vector3.new(0.45,0.4,9.3),CFrame.new(46,y,56),Enum.Material.WoodPlanks,pale,Enum.PartType.Block)
end
p('CastleStableHayBaleA',Vector3.new(4,2.5,3),CFrame.new(39,1.75,57)*CFrame.Angles(0,math.rad(12),0),Enum.Material.Grass,Color3.fromRGB(241,231,183),Enum.PartType.Block)
p('CastleStableHayBaleB',Vector3.new(3,2,3),CFrame.new(43,1.5,58)*CFrame.Angles(0,math.rad(-8),0),Enum.Material.Grass,Color3.fromRGB(236,224,181),Enum.PartType.Block)
for i,z in ipairs({35,53}) do
p('CastleKitchenGardenBed'..i,Vector3.new(6,0.6,12),CFrame.new(55,0.8,z),Enum.Material.Ground,pale,Enum.PartType.Block)
p('CastleKitchenGardenHerbs'..i,Vector3.new(3.5,1.2,10),CFrame.new(55,1.7,z),Enum.Material.LeafyGrass,Color3.fromRGB(206,231,188),Enum.PartType.Block)
end
report('Completed eighteen finishing parts: a fenced stable paddock, hay bales and two planted kitchen gardens behind and beside the castle.')
end)

run_chunk(15, function()
local count=0
local materials={}
local shapes={}
local names={}
local duplicates=0
for _,p in ipairs(workspace:GetDescendants()) do
if string.sub(p.Name,1,6)=='Castle' then
count=count+1
materials[tostring(p.Material)]=true
shapes[tostring(p.Shape)]=true
if names[p.Name] then duplicates=duplicates+1 end
names[p.Name]=true
end
end
local materialCount=0
local shapeCount=0
for _ in pairs(materials) do materialCount=materialCount+1 end
for _ in pairs(shapes) do shapeCount=shapeCount+1 end
report('Final castle inventory: '..count..' parts, '..materialCount..' materials, '..shapeCount..' shapes, '..duplicates..' duplicate names. Construction finished.')
end)
