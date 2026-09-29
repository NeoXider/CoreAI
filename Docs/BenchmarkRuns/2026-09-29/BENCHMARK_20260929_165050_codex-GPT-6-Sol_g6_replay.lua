-- Model-authored execute_lua calls in original order.
-- Model: codex/GPT-6 Sol; scenario: g6_free_build_vision; calls: 9
-- pcall keeps later calls running after a failed section, matching the benchmark.
local function run_chunk(index, body)
    local ok, err = pcall(body)
    if not ok then print('G6 chunk ' .. index .. ' failed: ' .. tostring(err)) end
end

run_chunk(1, function()
local function add(name,size,cf,material,color,shape)
 local p=Instance.new('Part')
 p.Name=name
 p.Size=size
 p.CFrame=cf
 p.Material=material
 if color then p.Color=color end
 p.Shape=shape
 p.Anchored=true
 p.Parent=workspace
end
local B=Enum.PartType.Block
local C=Enum.PartType.Cylinder
local pale=Color3.fromRGB(230,226,216)
add('CastleGround',Vector3.new(128,1,128),CFrame.new(0,0.5,0),Enum.Material.Grass,nil,B)
add('CastleCourtyardBase',Vector3.new(76,1,70),CFrame.new(0,1,0),Enum.Material.Sandstone,pale,B)
add('CastleNorthWall',Vector3.new(72,16,4),CFrame.new(0,9,33),Enum.Material.Limestone,pale,B)
add('CastleEastWall',Vector3.new(4,16,66),CFrame.new(36,9,0),Enum.Material.Limestone,pale,B)
add('CastleWestWall',Vector3.new(4,16,66),CFrame.new(-36,9,0),Enum.Material.Limestone,pale,B)
add('CastleSouthWallLeft',Vector3.new(29,16,4),CFrame.new(-21.5,9,-33),Enum.Material.Limestone,pale,B)
add('CastleSouthWallRight',Vector3.new(29,16,4),CFrame.new(21.5,9,-33),Enum.Material.Limestone,pale,B)
add('CastleTowerNW',Vector3.new(25,11,11),CFrame.new(-36,13,33)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Cobblestone,pale,C)
add('CastleTowerNE',Vector3.new(25,11,11),CFrame.new(36,13,33)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Cobblestone,pale,C)
add('CastleTowerSW',Vector3.new(25,11,11),CFrame.new(-36,13,-33)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Cobblestone,pale,C)
add('CastleTowerSE',Vector3.new(25,11,11),CFrame.new(36,13,-33)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Cobblestone,pale,C)
add('CastleGatehouseLeft',Vector3.new(6,23,11),CFrame.new(-9,12.5,-33),Enum.Material.Granite,pale,B)
add('CastleGatehouseRight',Vector3.new(6,23,11),CFrame.new(9,12.5,-33),Enum.Material.Granite,pale,B)
add('CastleGatehouseLintel',Vector3.new(24,5,11),CFrame.new(0,22.5,-33),Enum.Material.Granite,pale,B)
add('CastleKeepNorth',Vector3.new(28,27,3),CFrame.new(0,15,21),Enum.Material.Brick,pale,B)
add('CastleKeepSouth',Vector3.new(28,27,3),CFrame.new(0,15,1),Enum.Material.Brick,pale,B)
add('CastleKeepEast',Vector3.new(3,27,20),CFrame.new(14,15,11),Enum.Material.Brick,pale,B)
add('CastleKeepWest',Vector3.new(3,27,20),CFrame.new(-14,15,11),Enum.Material.Brick,pale,B)
add('CastleKeepRoofDeck',Vector3.new(30,2,22),CFrame.new(0,29.5,11),Enum.Material.Slate,nil,B)
add('CastleApproachRoad',Vector3.new(13,0.25,27),CFrame.new(0,1.15,-49),Enum.Material.Pavement,nil,B)
end)

run_chunk(2, function()
local function add(name,size,cf,material,color,shape)
 local p=Instance.new('Part')
 p.Name=name
 p.Size=size
 p.CFrame=cf
 p.Material=material
 if color then p.Color=color end
 p.Shape=shape
 p.Anchored=true
 p.Parent=workspace
end
local B=Enum.PartType.Block
local W=Enum.PartType.Wedge
local CW=Enum.PartType.CornerWedge
local pale=Color3.fromRGB(230,226,216)
add('CastleKeepRoofWest',Vector3.new(16,9,24),CFrame.new(-8,34.5,11)*CFrame.Angles(0,math.rad(90),0),Enum.Material.ClayRoofTiles,nil,W)
add('CastleKeepRoofEast',Vector3.new(16,9,24),CFrame.new(8,34.5,11)*CFrame.Angles(0,math.rad(-90),0),Enum.Material.ClayRoofTiles,nil,W)
add('CastleTowerRoofNW',Vector3.new(13,7,13),CFrame.new(-36,29,33),Enum.Material.RoofShingles,nil,CW)
add('CastleTowerRoofNE',Vector3.new(13,7,13),CFrame.new(36,29,33)*CFrame.Angles(0,math.rad(90),0),Enum.Material.RoofShingles,nil,CW)
add('CastleTowerRoofSE',Vector3.new(13,7,13),CFrame.new(36,29,-33)*CFrame.Angles(0,math.rad(180),0),Enum.Material.RoofShingles,nil,CW)
add('CastleTowerRoofSW',Vector3.new(13,7,13),CFrame.new(-36,29,-33)*CFrame.Angles(0,math.rad(270),0),Enum.Material.RoofShingles,nil,CW)
add('CastleGatePortcullisTop',Vector3.new(13,1,1),CFrame.new(0,16,-38.5),Enum.Material.Metal,nil,B)
add('CastleGatePortcullisBar1',Vector3.new(0.6,14,0.6),CFrame.new(-5,8.5,-38.5),Enum.Material.Metal,nil,B)
add('CastleGatePortcullisBar2',Vector3.new(0.6,14,0.6),CFrame.new(-2.5,8.5,-38.5),Enum.Material.Metal,nil,B)
add('CastleGatePortcullisBar3',Vector3.new(0.6,14,0.6),CFrame.new(0,8.5,-38.5),Enum.Material.Metal,nil,B)
add('CastleGatePortcullisBar4',Vector3.new(0.6,14,0.6),CFrame.new(2.5,8.5,-38.5),Enum.Material.Metal,nil,B)
add('CastleGatePortcullisBar5',Vector3.new(0.6,14,0.6),CFrame.new(5,8.5,-38.5),Enum.Material.Metal,nil,B)
add('CastleGatePortcullisCrossbar',Vector3.new(12,0.6,0.6),CFrame.new(0,7,-38.5),Enum.Material.Metal,nil,B)
add('CastleGateDrawbridge',Vector3.new(13,0.8,12),CFrame.new(0,1.8,-40),Enum.Material.WoodPlanks,nil,B)
add('CastleGateRamp',Vector3.new(13,1.5,6),CFrame.new(0,1.2,-47),Enum.Material.WoodPlanks,nil,W)
add('CastleKeepGreatDoor',Vector3.new(6,11,0.5),CFrame.new(0,7, -0.7),Enum.Material.Wood,nil,B)
end)

run_chunk(3, function()
local function add(n,s,c,m,col,sh) local p=Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m if col then p.Color=col end p.Shape=sh p.Anchored=true p.Parent=workspace end
local B=Enum.PartType.Block
local pale=Color3.fromRGB(230,226,216)
for i=1,7 do add('CastleNorthCrenel'..i,Vector3.new(5,3,4),CFrame.new(-27+(i-1)*9,18.5,33),Enum.Material.Limestone,pale,B) end
for i=1,4 do add('CastleEastCrenel'..i,Vector3.new(4,3,6),CFrame.new(36,18.5,-21+(i-1)*14),Enum.Material.Limestone,pale,B) end
for i=1,4 do add('CastleWestCrenel'..i,Vector3.new(4,3,6),CFrame.new(-36,18.5,-21+(i-1)*14),Enum.Material.Limestone,pale,B) end
for i=1,3 do add('CastleKeepWindow'..i,Vector3.new(3,5,0.4),CFrame.new(-8+(i-1)*8,18,-0.5),Enum.Material.Glass,Color3.fromRGB(93,151,190),B) end
end)

run_chunk(4, function()
local function add(n,s,c,m,col,sh) local p=Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m if col then p.Color=col end p.Shape=sh p.Anchored=true p.Parent=workspace end
local B=Enum.PartType.Block local C=Enum.PartType.Cylinder local W=Enum.PartType.Wedge
add('CastleWellStoneDrum',Vector3.new(3,7,7),CFrame.new(-20,3,-8)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Cobblestone,nil,C)
add('CastleWellWater',Vector3.new(0.2,5.4,5.4),CFrame.new(-20,4.6,-8)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Glass,Color3.fromRGB(65,142,205),C)
add('CastleWellStoneRim',Vector3.new(0.6,7.5,7.5),CFrame.new(-20,5,-8)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Marble,nil,C)
for i=1,4 do local x=-23+((i-1)%2)*6 local z=-11+math.floor((i-1)/2)*6 add('CastleWellCanopyPost'..i,Vector3.new(0.7,7,0.7),CFrame.new(x,8.5,z),Enum.Material.Wood,nil,B) end
add('CastleWellCanopyWest',Vector3.new(4,3,8),CFrame.new(-22,13,-8)*CFrame.Angles(0,math.rad(90),0),Enum.Material.RoofShingles,nil,W)
add('CastleWellCanopyEast',Vector3.new(4,3,8),CFrame.new(-18,13,-8)*CFrame.Angles(0,math.rad(-90),0),Enum.Material.RoofShingles,nil,W)
add('CastleForgeBody',Vector3.new(12,9,13),CFrame.new(23,5.8,-9),Enum.Material.Plaster,nil,B)
add('CastleForgeRoofLeft',Vector3.new(7,4,15),CFrame.new(19.5,12,-9)*CFrame.Angles(0,math.rad(90),0),Enum.Material.ClayRoofTiles,nil,W)
add('CastleForgeRoofRight',Vector3.new(7,4,15),CFrame.new(26.5,12,-9)*CFrame.Angles(0,math.rad(-90),0),Enum.Material.ClayRoofTiles,nil,W)
add('CastleForgeChimney',Vector3.new(8,2,2),CFrame.new(27,17,-12)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Brick,nil,C)
add('CastleMarketTable',Vector3.new(9,0.6,4),CFrame.new(-20,4,-23),Enum.Material.WoodPlanks,nil,B)
add('CastleMarketPostLeft',Vector3.new(0.5,8,0.5),CFrame.new(-24,6,-23),Enum.Material.Wood,nil,B)
add('CastleMarketPostRight',Vector3.new(0.5,8,0.5),CFrame.new(-16,6,-23),Enum.Material.Wood,nil,B)
add('CastleMarketAwning',Vector3.new(10,0.4,6),CFrame.new(-20,10,-23),Enum.Material.Fabric,Color3.fromRGB(178,35,43),B)
add('CastleMarketBarrel',Vector3.new(3,2,2),CFrame.new(-14,3,-26)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,nil,C)
end)

run_chunk(5, function()
local function add(n,s,c,m,col,sh) local p=Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m if col then p.Color=col end p.Shape=sh p.Anchored=true p.Parent=workspace end
local B=Enum.PartType.Block local C=Enum.PartType.Cylinder local W=Enum.PartType.Wedge local Ball=Enum.PartType.Ball
local blue=Color3.fromRGB(48,115,182)
add('CastleMoatEast',Vector3.new(9,0.3,88),CFrame.new(44,1.1,0),Enum.Material.Glass,blue,B)
add('CastleMoatWest',Vector3.new(9,0.3,88),CFrame.new(-44,1.1,0),Enum.Material.Glass,blue,B)
add('CastleMoatNorth',Vector3.new(80,0.3,9),CFrame.new(0,1.1,43),Enum.Material.Glass,blue,B)
add('CastleMoatSouthLeft',Vector3.new(34,0.3,9),CFrame.new(-24,1.1,-43),Enum.Material.Glass,blue,B)
add('CastleMoatSouthRight',Vector3.new(34,0.3,9),CFrame.new(24,1.1,-43),Enum.Material.Glass,blue,B)
add('CastleBridgeRailLeft',Vector3.new(0.6,2,12),CFrame.new(-6.5,3,-40),Enum.Material.Wood,nil,B)
add('CastleBridgeRailRight',Vector3.new(0.6,2,12),CFrame.new(6.5,3,-40),Enum.Material.Wood,nil,B)
add('CastleTreeTrunkWest',Vector3.new(11,2,2),CFrame.new(-55,6,-53)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,nil,C)
add('CastleTreeCrownWest',Vector3.new(13,13,13),CFrame.new(-55,16,-53),Enum.Material.LeafyGrass,nil,Ball)
add('CastleTreeTrunkEast',Vector3.new(10,2,2),CFrame.new(54,6,51)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,nil,C)
add('CastleTreeCrownEast',Vector3.new(12,12,12),CFrame.new(54,15,51),Enum.Material.LeafyGrass,nil,Ball)
add('CastleGateBannerLeft',Vector3.new(2.5,7,0.2),CFrame.new(-9,22,-39),Enum.Material.Fabric,Color3.fromRGB(26,63,173),B)
add('CastleGateBannerRight',Vector3.new(2.5,7,0.2),CFrame.new(9,22,-39),Enum.Material.Fabric,Color3.fromRGB(26,63,173),B)
add('CastleGateLanternLeft',Vector3.new(1.7,1.7,1.7),CFrame.new(-13,12,-39),Enum.Material.Neon,Color3.fromRGB(255,188,65),Ball)
add('CastleGateLanternRight',Vector3.new(1.7,1.7,1.7),CFrame.new(13,12,-39),Enum.Material.Neon,Color3.fromRGB(255,188,65),Ball)
add('CastleOuterCottageBody',Vector3.new(11,8,11),CFrame.new(55,5,9),Enum.Material.Plaster,nil,B)
add('CastleOuterCottageRoofLeft',Vector3.new(6,4,13),CFrame.new(52,11,9)*CFrame.Angles(0,math.rad(90),0),Enum.Material.RoofShingles,nil,W)
add('CastleOuterCottageRoofRight',Vector3.new(6,4,13),CFrame.new(58,11,9)*CFrame.Angles(0,math.rad(-90),0),Enum.Material.RoofShingles,nil,W)
end)

run_chunk(6, function()
local function add(n,s,c,m,col,sh) local p=Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m if col then p.Color=col end p.Shape=sh p.Anchored=true p.Parent=workspace end
local CW=Enum.PartType.CornerWedge local B=Enum.PartType.Block local Ball=Enum.PartType.Ball
local towers={{'NW',-36,33},{'NE',36,33},{'SE',36,-33},{'SW',-36,-33}}
for _,t in ipairs(towers) do for j=1,3 do add('CastleTowerRoof'..t[1]..'Quarter'..j,Vector3.new(13,7,13),CFrame.new(t[2],29,t[3])*CFrame.Angles(0,math.rad(j*90),0),Enum.Material.RoofShingles,nil,CW) end end
for _,t in ipairs(towers) do add('CastleTowerFinial'..t[1],Vector3.new(2,2,2),CFrame.new(t[2],34,t[3]),Enum.Material.Metal,Color3.fromRGB(227,203,117),Ball) end
end)

run_chunk(7, function()
local function add(n,s,c,m,col,sh) local p=Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m if col then p.Color=col end p.Shape=sh p.Anchored=true p.Parent=workspace end
local B=Enum.PartType.Block local C=Enum.PartType.Cylinder local W=Enum.PartType.Wedge local Ball=Enum.PartType.Ball
add('CastleStableBody',Vector3.new(13,8,14),CFrame.new(25,5.5,18),Enum.Material.WoodPlanks,nil,B)
add('CastleStableRoofWest',Vector3.new(7,4,16),CFrame.new(21.5,11,18)*CFrame.Angles(0,math.rad(90),0),Enum.Material.RoofShingles,nil,W)
add('CastleStableRoofEast',Vector3.new(7,4,16),CFrame.new(28.5,11,18)*CFrame.Angles(0,math.rad(-90),0),Enum.Material.RoofShingles,nil,W)
add('CastleStableDoor',Vector3.new(4,6,0.4),CFrame.new(25,4.5,10.8),Enum.Material.Wood,nil,B)
add('CastleStableHay',Vector3.new(4,2,3),CFrame.new(31,2.8,8),Enum.Material.Fabric,Color3.fromRGB(202,169,71),B)
add('CastleGardenBedLeft',Vector3.new(5,0.4,12),CFrame.new(-27,1.8,13),Enum.Material.Ground,nil,B)
add('CastleGardenBedRight',Vector3.new(5,0.4,12),CFrame.new(-20,1.8,13),Enum.Material.Ground,nil,B)
for i=1,6 do add('CastleGardenShrub'..i,Vector3.new(2.2,2.2,2.2),CFrame.new(-27+((i-1)%2)*7,3,8+math.floor((i-1)/2)*4),Enum.Material.LeafyGrass,nil,Ball) end
add('CastleCourtyardBenchSeat',Vector3.new(7,0.5,1.5),CFrame.new(-10,3,-21),Enum.Material.WoodPlanks,nil,B)
add('CastleCourtyardBenchLegLeft',Vector3.new(0.5,1.5,1.5),CFrame.new(-13,2,-21),Enum.Material.Wood,nil,B)
add('CastleCourtyardBenchLegRight',Vector3.new(0.5,1.5,1.5),CFrame.new(-7,2,-21),Enum.Material.Wood,nil,B)
end)

run_chunk(8, function()
local function add(n,s,c,m,col,sh) local p=Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m if col then p.Color=col end p.Shape=sh p.Anchored=true p.Parent=workspace end
local B=Enum.PartType.Block local C=Enum.PartType.Cylinder local Ball=Enum.PartType.Ball
add('CastleKeepFlagPole',Vector3.new(13,0.4,0.4),CFrame.new(0,44,11)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Metal,nil,C)
add('CastleKeepFlag',Vector3.new(7,4,0.2),CFrame.new(3.5,48,11),Enum.Material.Fabric,Color3.fromRGB(28,65,171),B)
add('CastleKeepFlagEmblem',Vector3.new(1.4,1.4,0.25),CFrame.new(3,48,10.8),Enum.Material.SmoothPlastic,Color3.fromRGB(239,190,49),Ball)
add('CastleKeepWindowFrameLeft',Vector3.new(0.4,5.4,0.5),CFrame.new(-9.7,18,-0.3),Enum.Material.Sandstone,nil,B)
add('CastleKeepWindowFrameRight',Vector3.new(0.4,5.4,0.5),CFrame.new(9.7,18,-0.3),Enum.Material.Sandstone,nil,B)
add('CastleKeepDoorStep',Vector3.new(8,0.6,3),CFrame.new(0,1.9,-2),Enum.Material.Granite,nil,B)
add('CastleForgeFire',Vector3.new(2,2,2),CFrame.new(17,3,-9),Enum.Material.Neon,Color3.fromRGB(255,105,28),Ball)
add('CastleForgeAnvil',Vector3.new(3,1.2,1.5),CFrame.new(17,3,-13),Enum.Material.Metal,nil,B)
add('CastleCourtyardCrateOne',Vector3.new(3,3,3),CFrame.new(19,3,-23),Enum.Material.WoodPlanks,nil,B)
add('CastleCourtyardCrateTwo',Vector3.new(2.5,2.5,2.5),CFrame.new(22,2.8,-25),Enum.Material.WoodPlanks,nil,B)
add('CastleOuterRockWest',Vector3.new(7,4,6),CFrame.new(-55,2.5,22)*CFrame.Angles(0,math.rad(23),0),Enum.Material.Rock,nil,B)
add('CastleOuterRockEast',Vector3.new(6,3,5),CFrame.new(54,2,35)*CFrame.Angles(0,math.rad(-31),0),Enum.Material.Rock,nil,B)
add('CastleApproachMarkerLeft',Vector3.new(7,2,2),CFrame.new(-9,4,-54)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Basalt,nil,C)
add('CastleApproachMarkerRight',Vector3.new(7,2,2),CFrame.new(9,4,-54)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Basalt,nil,C)
add('CastleApproachLampLeft',Vector3.new(1.5,1.5,1.5),CFrame.new(-9,8,-54),Enum.Material.Neon,Color3.fromRGB(255,201,87),Ball)
add('CastleApproachLampRight',Vector3.new(1.5,1.5,1.5),CFrame.new(9,8,-54),Enum.Material.Neon,Color3.fromRGB(255,201,87),Ball)
end)

run_chunk(9, function()
local function add(n,s,c,m,col,sh) local p=Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m if col then p.Color=col end p.Shape=sh p.Anchored=true p.Parent=workspace end
local B=Enum.PartType.Block local C=Enum.PartType.Cylinder local Ball=Enum.PartType.Ball local pale=Color3.fromRGB(230,226,216)
for i=1,3 do add('CastleSouthCrenelLeft'..i,Vector3.new(5,3,4),CFrame.new(-31+(i-1)*9,18.5,-33),Enum.Material.Limestone,pale,B) end
for i=1,3 do add('CastleSouthCrenelRight'..i,Vector3.new(5,3,4),CFrame.new(13+(i-1)*9,18.5,-33),Enum.Material.Limestone,pale,B) end
add('CastleGatehouseCrenelLeft',Vector3.new(4,3,11),CFrame.new(-10,26.5,-33),Enum.Material.Granite,pale,B)
add('CastleGatehouseCrenelCenter',Vector3.new(4,3,11),CFrame.new(0,26.5,-33),Enum.Material.Granite,pale,B)
add('CastleGatehouseCrenelRight',Vector3.new(4,3,11),CFrame.new(10,26.5,-33),Enum.Material.Granite,pale,B)
add('CastleKeepParapetNorth',Vector3.new(30,3,1),CFrame.new(0,31,22),Enum.Material.Slate,nil,B)
add('CastleKeepParapetSouth',Vector3.new(30,3,1),CFrame.new(0,31,0),Enum.Material.Slate,nil,B)
add('CastleForgeChimneyCap',Vector3.new(3,1,3),CFrame.new(27,21,-12),Enum.Material.Basalt,nil,B)
add('CastleApproachMilestone',Vector3.new(5,2,2),CFrame.new(-12,3,-59)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Limestone,pale,C)
add('CastleApproachMilestoneFinial',Vector3.new(2,2,2),CFrame.new(-12,6,-59),Enum.Material.Marble,nil,Ball)
add('CastleMarketCrate',Vector3.new(3,2,3),CFrame.new(-25,2.5,-27),Enum.Material.WoodPlanks,nil,B)
add('CastleStableWaterTrough',Vector3.new(6,1.5,2),CFrame.new(17,2.6,23),Enum.Material.Wood,nil,B)
end)
