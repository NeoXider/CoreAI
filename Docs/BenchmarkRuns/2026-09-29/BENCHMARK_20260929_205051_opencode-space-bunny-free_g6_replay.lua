-- Model-authored execute_lua calls in original order.
-- Model: opencode/space-bunny-free; scenario: g6_free_build_vision; calls: 6
-- pcall keeps later calls running after a failed section, matching the benchmark.
local function run_chunk(index, body)
    local ok, err = pcall(body)
    if not ok then print('G6 chunk ' .. index .. ' failed: ' .. tostring(err)) end
end

run_chunk(1, function()
local function make(name,size,cframe,material,color,shape)
 local p=Instance.new('Part')
 p.Name=name
 p.Size=size
 p.CFrame=cframe
 p.Material=material
 p.Color=color
 p.Shape=shape
 p.Anchored=true
 p.CanCollide=true
 p.Parent=workspace
end
make('CastleGround',Vector3.new(128,2,128),CFrame.new(0,-1,0),Enum.Material.Grass,Color3.fromRGB(202,210,166),Enum.PartType.Block)
make('CastleMoatNorth',Vector3.new(104,0.3,8),CFrame.new(0,0.1,-47),Enum.Material.Glass,Color3.fromRGB(135,218,232),Enum.PartType.Block)
make('CastleMoatSouthWest',Vector3.new(44,0.3,8),CFrame.new(-30,0.1,47),Enum.Material.Glass,Color3.fromRGB(135,218,232),Enum.PartType.Block)
make('CastleMoatSouthEast',Vector3.new(44,0.3,8),CFrame.new(30,0.1,47),Enum.Material.Glass,Color3.fromRGB(135,218,232),Enum.PartType.Block)
make('CastleMoatWest',Vector3.new(8,0.3,64),CFrame.new(-47,0.1,0),Enum.Material.Glass,Color3.fromRGB(135,218,232),Enum.PartType.Block)
make('CastleMoatEast',Vector3.new(8,0.3,64),CFrame.new(47,0.1,0),Enum.Material.Glass,Color3.fromRGB(135,218,232),Enum.PartType.Block)
make('CastleWallNorth',Vector3.new(64,11,3),CFrame.new(0,5.5,-32),Enum.Material.Cobblestone,Color3.fromRGB(216,211,199),Enum.PartType.Block)
make('CastleWallWest',Vector3.new(3,11,64),CFrame.new(-38,5.5,1),Enum.Material.Cobblestone,Color3.fromRGB(216,211,199),Enum.PartType.Block)
make('CastleWallEast',Vector3.new(3,11,64),CFrame.new(38,5.5,1),Enum.Material.Cobblestone,Color3.fromRGB(216,211,199),Enum.PartType.Block)
make('CastleWallSouthWest',Vector3.new(26,11,3),CFrame.new(-23,5.5,34),Enum.Material.Cobblestone,Color3.fromRGB(216,211,199),Enum.PartType.Block)
make('CastleWallSouthEast',Vector3.new(26,11,3),CFrame.new(23,5.5,34),Enum.Material.Cobblestone,Color3.fromRGB(216,211,199),Enum.PartType.Block)
make('CastleTowerNW',Vector3.new(11,18,11),CFrame.new(-38,9,-30)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Limestone,Color3.fromRGB(225,221,207),Enum.PartType.Cylinder)
make('CastleTowerNE',Vector3.new(11,18,11),CFrame.new(38,9,-30)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Limestone,Color3.fromRGB(225,221,207),Enum.PartType.Cylinder)
make('CastleTowerSW',Vector3.new(11,18,11),CFrame.new(-38,9,30)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Limestone,Color3.fromRGB(225,221,207),Enum.PartType.Cylinder)
make('CastleTowerSE',Vector3.new(11,18,11),CFrame.new(38,9,30)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Limestone,Color3.fromRGB(225,221,207),Enum.PartType.Cylinder)
make('CastleGateTowerWest',Vector3.new(8,15,8),CFrame.new(-10,7.5,34)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Brick,Color3.fromRGB(224,201,175),Enum.PartType.Cylinder)
make('CastleGateTowerEast',Vector3.new(8,15,8),CFrame.new(10,7.5,34)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Brick,Color3.fromRGB(224,201,175),Enum.PartType.Cylinder)
make('CastleKeep',Vector3.new(30,22,24),CFrame.new(0,11,-10),Enum.Material.Plaster,Color3.fromRGB(232,223,199),Enum.PartType.Block)
make('CastleKeepUpper',Vector3.new(24,8,18),CFrame.new(0,25,-10),Enum.Material.Limestone,Color3.fromRGB(226,218,195),Enum.PartType.Block)
make('CastleKeepRoofWest',Vector3.new(15,7,22),CFrame.new(-7.5,32.5,-10),Enum.Material.ClayRoofTiles,Color3.fromRGB(220,156,115),Enum.PartType.Wedge)
make('CastleKeepRoofEast',Vector3.new(15,7,22),CFrame.new(7.5,32.5,-10)*CFrame.Angles(0,math.rad(180),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(220,156,115),Enum.PartType.Wedge)
end)

run_chunk(2, function()
local function make(name,size,cframe,material,color,shape)
 local p=Instance.new('Part')
 p.Name=name
 p.Size=size
 p.CFrame=cframe
 p.Material=material
 p.Color=color
 p.Shape=shape
 p.Anchored=true
 p.CanCollide=true
 p.Parent=workspace
end
make('CastleTowerNWRoofQ1',Vector3.new(7,7,7),CFrame.new(-41.5,21.5,-33.5),Enum.Material.ClayRoofTiles,Color3.fromRGB(232,184,143),Enum.PartType.CornerWedge)
make('CastleTowerNWRoofQ2',Vector3.new(7,7,7),CFrame.new(-34.5,21.5,-33.5)*CFrame.Angles(0,math.rad(90),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(232,184,143),Enum.PartType.CornerWedge)
make('CastleTowerNWRoofQ3',Vector3.new(7,7,7),CFrame.new(-34.5,21.5,-26.5)*CFrame.Angles(0,math.rad(180),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(232,184,143),Enum.PartType.CornerWedge)
make('CastleTowerNWRoofQ4',Vector3.new(7,7,7),CFrame.new(-41.5,21.5,-26.5)*CFrame.Angles(0,math.rad(270),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(232,184,143),Enum.PartType.CornerWedge)
make('CastleTowerNERoofQ1',Vector3.new(7,7,7),CFrame.new(34.5,21.5,-33.5),Enum.Material.Slate,Color3.fromRGB(205,213,218),Enum.PartType.CornerWedge)
make('CastleTowerNERoofQ2',Vector3.new(7,7,7),CFrame.new(41.5,21.5,-33.5)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Slate,Color3.fromRGB(205,213,218),Enum.PartType.CornerWedge)
make('CastleTowerNERoofQ3',Vector3.new(7,7,7),CFrame.new(41.5,21.5,-26.5)*CFrame.Angles(0,math.rad(180),0),Enum.Material.Slate,Color3.fromRGB(205,213,218),Enum.PartType.CornerWedge)
make('CastleTowerNERoofQ4',Vector3.new(7,7,7),CFrame.new(34.5,21.5,-26.5)*CFrame.Angles(0,math.rad(270),0),Enum.Material.Slate,Color3.fromRGB(205,213,218),Enum.PartType.CornerWedge)
make('CastleTowerSWRoofQ1',Vector3.new(7,7,7),CFrame.new(-41.5,21.5,26.5),Enum.Material.ClayRoofTiles,Color3.fromRGB(232,184,143),Enum.PartType.CornerWedge)
make('CastleTowerSWRoofQ2',Vector3.new(7,7,7),CFrame.new(-34.5,21.5,26.5)*CFrame.Angles(0,math.rad(90),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(232,184,143),Enum.PartType.CornerWedge)
make('CastleTowerSWRoofQ3',Vector3.new(7,7,7),CFrame.new(-34.5,21.5,33.5)*CFrame.Angles(0,math.rad(180),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(232,184,143),Enum.PartType.CornerWedge)
make('CastleTowerSWRoofQ4',Vector3.new(7,7,7),CFrame.new(-41.5,21.5,33.5)*CFrame.Angles(0,math.rad(270),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(232,184,143),Enum.PartType.CornerWedge)
make('CastleTowerSERoofQ1',Vector3.new(7,7,7),CFrame.new(34.5,21.5,26.5),Enum.Material.Slate,Color3.fromRGB(205,213,218),Enum.PartType.CornerWedge)
make('CastleTowerSERoofQ2',Vector3.new(7,7,7),CFrame.new(41.5,21.5,26.5)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Slate,Color3.fromRGB(205,213,218),Enum.PartType.CornerWedge)
make('CastleTowerSERoofQ3',Vector3.new(7,7,7),CFrame.new(41.5,21.5,33.5)*CFrame.Angles(0,math.rad(180),0),Enum.Material.Slate,Color3.fromRGB(205,213,218),Enum.PartType.CornerWedge)
make('CastleTowerSERoofQ4',Vector3.new(7,7,7),CFrame.new(34.5,21.5,33.5)*CFrame.Angles(0,math.rad(270),0),Enum.Material.Slate,Color3.fromRGB(205,213,218),Enum.PartType.CornerWedge)
report('Added four tiled and four slate conical tower roofs from corner wedges.')
end)

run_chunk(3, function()
local towerFixes={{'CastleTowerNW',Vector3.new(20,11,11),CFrame.new(-38,10,-30)*CFrame.Angles(0,0,math.rad(90))},{'CastleTowerNE',Vector3.new(20,11,11),CFrame.new(38,10,-30)*CFrame.Angles(0,0,math.rad(90))},{'CastleTowerSW',Vector3.new(20,11,11),CFrame.new(-38,10,30)*CFrame.Angles(0,0,math.rad(90))},{'CastleTowerSE',Vector3.new(20,11,11),CFrame.new(38,10,30)*CFrame.Angles(0,0,math.rad(90))},{'CastleGateTowerWest',Vector3.new(17,8,8),CFrame.new(-10,8.5,34)*CFrame.Angles(0,0,math.rad(90))},{'CastleGateTowerEast',Vector3.new(17,8,8),CFrame.new(10,8.5,34)*CFrame.Angles(0,0,math.rad(90))}}
for _,v in ipairs(towerFixes) do
 local p=workspace:FindFirstChild(v[1])
 p.Size=v[2]
 p.CFrame=v[3]
end
local function make(name,size,cframe,material,color,shape)
 local p=Instance.new('Part')
 p.Name=name
 p.Size=size
 p.CFrame=cframe
 p.Material=material
 p.Color=color
 p.Shape=shape
 p.Anchored=true
 p.CanCollide=true
 p.Parent=workspace
end
make('CastleNorthBattlement01',Vector3.new(3,3,4),CFrame.new(-30,12.5,-32),Enum.Material.Limestone,Color3.fromRGB(226,221,207),Enum.PartType.Block)
make('CastleNorthBattlement02',Vector3.new(3,3,4),CFrame.new(-21,12.5,-32),Enum.Material.Limestone,Color3.fromRGB(226,221,207),Enum.PartType.Block)
make('CastleNorthBattlement03',Vector3.new(3,3,4),CFrame.new(-12,12.5,-32),Enum.Material.Limestone,Color3.fromRGB(226,221,207),Enum.PartType.Block)
make('CastleNorthBattlement04',Vector3.new(3,3,4),CFrame.new(-3,12.5,-32),Enum.Material.Limestone,Color3.fromRGB(226,221,207),Enum.PartType.Block)
make('CastleNorthBattlement05',Vector3.new(3,3,4),CFrame.new(6,12.5,-32),Enum.Material.Limestone,Color3.fromRGB(226,221,207),Enum.PartType.Block)
make('CastleNorthBattlement06',Vector3.new(3,3,4),CFrame.new(15,12.5,-32),Enum.Material.Limestone,Color3.fromRGB(226,221,207),Enum.PartType.Block)
make('CastleNorthBattlement07',Vector3.new(3,3,4),CFrame.new(24,12.5,-32),Enum.Material.Limestone,Color3.fromRGB(226,221,207),Enum.PartType.Block)
make('CastleSouthWestBattlement01',Vector3.new(3,3,4),CFrame.new(-34,12.5,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleSouthWestBattlement02',Vector3.new(3,3,4),CFrame.new(-25,12.5,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleSouthWestBattlement03',Vector3.new(3,3,4),CFrame.new(-16,12.5,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleSouthEastBattlement01',Vector3.new(3,3,4),CFrame.new(16,12.5,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleSouthEastBattlement02',Vector3.new(3,3,4),CFrame.new(25,12.5,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleSouthEastBattlement03',Vector3.new(3,3,4),CFrame.new(34,12.5,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleWallWalkNorth',Vector3.new(64,0.6,3.5),CFrame.new(0,11.1,-32),Enum.Material.Pavement,Color3.fromRGB(218,214,202),Enum.PartType.Block)
make('CastleWallWalkWest',Vector3.new(3.5,0.6,64),CFrame.new(-38,11.1,0),Enum.Material.Pavement,Color3.fromRGB(218,214,202),Enum.PartType.Block)
make('CastleWallWalkEast',Vector3.new(3.5,0.6,64),CFrame.new(38,11.1,0),Enum.Material.Pavement,Color3.fromRGB(218,214,202),Enum.PartType.Block)
report('Corrected tower drum proportions and added continuous battlements and wall walks.')
end)

run_chunk(4, function()
local function make(name,size,cframe,material,color,shape)
 local p=Instance.new('Part')
 p.Name=name
 p.Size=size
 p.CFrame=cframe
 p.Material=material
 p.Color=color
 p.Shape=shape
 p.Anchored=true
 p.CanCollide=true
 p.Parent=workspace
end
make('CastleDrawbridgeDeck',Vector3.new(12,0.7,17),CFrame.new(0,0.45,43.5),Enum.Material.WoodPlanks,Color3.fromRGB(224,216,188),Enum.PartType.Block)
make('CastleDrawbridgeRailWest',Vector3.new(0.6,2.2,17),CFrame.new(-6.1,1.8,43.5),Enum.Material.Wood,Color3.fromRGB(218,205,170),Enum.PartType.Block)
make('CastleDrawbridgeRailEast',Vector3.new(0.6,2.2,17),CFrame.new(6.1,1.8,43.5),Enum.Material.Wood,Color3.fromRGB(218,205,170),Enum.PartType.Block)
make('CastleGateJambWest',Vector3.new(2.2,8.5,4),CFrame.new(-5.2,4.25,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleGateJambEast',Vector3.new(2.2,8.5,4),CFrame.new(5.2,4.25,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleGateLintelWest',Vector3.new(3.6,2,4),CFrame.new(-4,9.25,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleGateLintelCenter',Vector3.new(3.6,2,4),CFrame.new(0,9.25,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleGateLintelEast',Vector3.new(3.6,2,4),CFrame.new(4,9.25,34),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleGateParapet',Vector3.new(18,2.5,4),CFrame.new(0,11.75,34),Enum.Material.Sandstone,Color3.fromRGB(230,211,177),Enum.PartType.Block)
make('CastlePortcullisTopRail',Vector3.new(8.4,0.4,0.4),CFrame.new(0,8.1,35.2),Enum.Material.Metal,Color3.fromRGB(168,172,170),Enum.PartType.Cylinder)
make('CastlePortcullisBottomRail',Vector3.new(8.4,0.4,0.4),CFrame.new(0,1.5,35.2),Enum.Material.Metal,Color3.fromRGB(168,172,170),Enum.PartType.Cylinder)
make('CastlePortcullisBar01',Vector3.new(8.4,0.35,0.35),CFrame.new(-3.6,4.5,35.2)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.CorrodedMetal,Color3.fromRGB(205,190,171),Enum.PartType.Cylinder)
make('CastlePortcullisBar02',Vector3.new(8.4,0.35,0.35),CFrame.new(-1.8,4.5,35.2)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.CorrodedMetal,Color3.fromRGB(205,190,171),Enum.PartType.Cylinder)
make('CastlePortcullisBar03',Vector3.new(8.4,0.35,0.35),CFrame.new(0,4.5,35.2)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.CorrodedMetal,Color3.fromRGB(205,190,171),Enum.PartType.Cylinder)
make('CastlePortcullisBar04',Vector3.new(8.4,0.35,0.35),CFrame.new(1.8,4.5,35.2)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.CorrodedMetal,Color3.fromRGB(205,190,171),Enum.PartType.Cylinder)
make('CastlePortcullisBar05',Vector3.new(8.4,0.35,0.35),CFrame.new(3.6,4.5,35.2)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.CorrodedMetal,Color3.fromRGB(205,190,171),Enum.PartType.Cylinder)
make('CastlePortcullisCrossbarLow',Vector3.new(8.4,0.4,0.4),CFrame.new(0,2.3,35.2),Enum.Material.CorrodedMetal,Color3.fromRGB(205,190,171),Enum.PartType.Cylinder)
make('CastlePortcullisCrossbarMid',Vector3.new(8.4,0.4,0.4),CFrame.new(0,4.5,35.2),Enum.Material.CorrodedMetal,Color3.fromRGB(205,190,171),Enum.PartType.Cylinder)
make('CastlePortcullisCrossbarHigh',Vector3.new(8.4,0.4,0.4),CFrame.new(0,6.7,35.2),Enum.Material.CorrodedMetal,Color3.fromRGB(205,190,171),Enum.PartType.Cylinder)
make('CastleDrawbridgeCrossBeam',Vector3.new(12.8,0.5,0.7),CFrame.new(0,0.25,51.7),Enum.Material.Metal,Color3.fromRGB(174,166,150),Enum.PartType.Block)
report('Completed the fortified gatehouse with sandstone arch, metal portcullis and timber drawbridge.')
end)

run_chunk(5, function()
local function make(name,size,cframe,material,color,shape)
 local p=Instance.new('Part')
 p.Name=name
 p.Size=size
 p.CFrame=cframe
 p.Material=material
 p.Color=color
 p.Shape=shape
 p.Anchored=true
 p.CanCollide=true
 p.Parent=workspace
end
make('CastleKeepWindowSouthWestFrame',Vector3.new(3,3.8,0.4),CFrame.new(-10,8,2.15),Enum.Material.Limestone,Color3.fromRGB(229,223,207),Enum.PartType.Block)
make('CastleKeepWindowSouthWestGlass',Vector3.new(2.2,3,0.2),CFrame.new(-10,8,2.42),Enum.Material.Glass,Color3.fromRGB(151,211,228),Enum.PartType.Block)
make('CastleKeepWindowSouthWestSill',Vector3.new(3.6,0.4,1),CFrame.new(-10,5.9,2.45),Enum.Material.Marble,Color3.fromRGB(238,237,226),Enum.PartType.Block)
make('CastleKeepWindowSouthEastFrame',Vector3.new(3,3.8,0.4),CFrame.new(10,8,2.15),Enum.Material.Limestone,Color3.fromRGB(229,223,207),Enum.PartType.Block)
make('CastleKeepWindowSouthEastGlass',Vector3.new(2.2,3,0.2),CFrame.new(10,8,2.42),Enum.Material.Glass,Color3.fromRGB(151,211,228),Enum.PartType.Block)
make('CastleKeepWindowSouthEastSill',Vector3.new(3.6,0.4,1),CFrame.new(10,5.9,2.45),Enum.Material.Marble,Color3.fromRGB(238,237,226),Enum.PartType.Block)
make('CastleKeepWindowUpperFrame',Vector3.new(3.2,4,0.4),CFrame.new(0,24.5,0.15),Enum.Material.Brick,Color3.fromRGB(229,211,185),Enum.PartType.Block)
make('CastleKeepWindowUpperGlass',Vector3.new(2.3,3.1,0.2),CFrame.new(0,24.5,0.42),Enum.Material.Glass,Color3.fromRGB(151,211,228),Enum.PartType.Block)
make('CastleKeepWindowUpperSill',Vector3.new(3.8,0.4,1),CFrame.new(0,22.2,0.5),Enum.Material.Limestone,Color3.fromRGB(229,223,207),Enum.PartType.Block)
make('CastleKeepGreatDoor',Vector3.new(3.2,5.5,0.55),CFrame.new(0,2.8,2.3),Enum.Material.Wood,Color3.fromRGB(222,202,166),Enum.PartType.Block)
make('CastleKeepThreshold',Vector3.new(4.5,0.35,2.5),CFrame.new(0,0.2,2.6),Enum.Material.Marble,Color3.fromRGB(238,237,226),Enum.PartType.Block)
make('CastleKeepWindowWestFrame',Vector3.new(3,3.8,0.4),CFrame.new(-15.2,10,-7)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Limestone,Color3.fromRGB(229,223,207),Enum.PartType.Block)
make('CastleKeepWindowWestGlass',Vector3.new(2.2,3,0.2),CFrame.new(-15.48,10,-7)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Glass,Color3.fromRGB(151,211,228),Enum.PartType.Block)
make('CastleKeepWindowEastFrame',Vector3.new(3,3.8,0.4),CFrame.new(15.2,15,-16)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Limestone,Color3.fromRGB(229,223,207),Enum.PartType.Block)
make('CastleKeepWindowEastGlass',Vector3.new(2.2,3,0.2),CFrame.new(15.48,15,-16)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Glass,Color3.fromRGB(151,211,228),Enum.PartType.Block)
make('CastleKeepFlagpole',Vector3.new(13,0.35,0.35),CFrame.new(0,43,-10)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Metal,Color3.fromRGB(210,215,214),Enum.PartType.Cylinder)
make('CastleKeepBanner',Vector3.new(5,2.8,0.18),CFrame.new(2.8,46.3,-10),Enum.Material.Fabric,Color3.fromRGB(190,42,55),Enum.PartType.Wedge)
make('CastleKeepBannerFinial',Vector3.new(1.3,1.3,1.3),CFrame.new(0,50,-10),Enum.Material.Gold,Color3.fromRGB(255,218,82),Enum.PartType.Ball)
make('CastleKeepChimney',Vector3.new(3,8,3),CFrame.new(11,39,-10),Enum.Material.Brick,Color3.fromRGB(224,201,175),Enum.PartType.Block)
make('CastleKeepChimneyCap',Vector3.new(4,0.7,4),CFrame.new(11,43.2,-10),Enum.Material.Slate,Color3.fromRGB(205,213,218),Enum.PartType.Block)
report('Detailed the keep with glazed windows, carved sills, a great door, chimney, and rooftop banner.')
end)

run_chunk(6, function()
local function make(name,size,cframe,material,color,shape)
 if workspace:FindFirstChild(name) then return end
 local p=Instance.new('Part')
 p.Name=name
 p.Size=size
 p.CFrame=cframe
 p.Material=material
 p.Color=color
 p.Shape=shape
 p.Anchored=true
 p.CanCollide=true
 p.Parent=workspace
end
make('CastleKeepBannerFinial',Vector3.new(1.3,1.3,1.3),CFrame.new(0,50,-10),Enum.Material.Metal,Color3.fromRGB(255,218,82),Enum.PartType.Ball)
make('CastleCourtyardFloor',Vector3.new(28,0.4,27),CFrame.new(0,0.2,0),Enum.Material.Pavement,Color3.fromRGB(218,214,202),Enum.PartType.Block)
make('CastleCourtyardPath',Vector3.new(8,0.3,30),CFrame.new(0,0.5,16),Enum.Material.Cobblestone,Color3.fromRGB(216,211,199),Enum.PartType.Block)
make('CastleWellRimNorth',Vector3.new(6,1.4,1),CFrame.new(-8,1.4,5),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleWellRimSouth',Vector3.new(6,1.4,1),CFrame.new(-8,1.4,-1),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleWellRimWest',Vector3.new(1,1.4,6),CFrame.new(-10.5,1.4,2),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleWellRimEast',Vector3.new(1,1.4,6),CFrame.new(-5.5,1.4,2),Enum.Material.Sandstone,Color3.fromRGB(232,216,184),Enum.PartType.Block)
make('CastleWellWater',Vector3.new(0.3,4.5,4.5),CFrame.new(-8,1.4,2)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Glass,Color3.fromRGB(135,218,232),Enum.PartType.Cylinder)
make('CastleWellPostWest',Vector3.new(0.6,4.5,0.6),CFrame.new(-10,2.5,2)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,Color3.fromRGB(218,205,170),Enum.PartType.Cylinder)
make('CastleWellPostEast',Vector3.new(0.6,4.5,0.6),CFrame.new(-6,2.5,2)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,Color3.fromRGB(218,205,170),Enum.PartType.Cylinder)
make('CastleWellRoofWest',Vector3.new(7,3,7),CFrame.new(-8,5.4,2),Enum.Material.ClayRoofTiles,Color3.fromRGB(220,156,115),Enum.PartType.Wedge)
make('CastleWellRoofEast',Vector3.new(7,3,7),CFrame.new(-8,5.4,2)*CFrame.Angles(0,math.rad(180),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(220,156,115),Enum.PartType.Wedge)
make('CastleMarketCanopyWest',Vector3.new(9,3,6),CFrame.new(-18,4,8),Enum.Material.Fabric,Color3.fromRGB(173,61,70),Enum.PartType.Wedge)
make('CastleMarketCanopyEast',Vector3.new(9,3,6),CFrame.new(15,4,9)*CFrame.Angles(0,math.rad(180),0),Enum.Material.Fabric,Color3.fromRGB(58,102,158),Enum.PartType.Wedge)
make('CastleMarketCounterWest',Vector3.new(8,1,3),CFrame.new(-18,1.1,8),Enum.Material.WoodPlanks,Color3.fromRGB(224,216,188),Enum.PartType.Block)
make('CastleMarketCounterEast',Vector3.new(8,1,3),CFrame.new(15,1.1,9),Enum.Material.WoodPlanks,Color3.fromRGB(224,216,188),Enum.PartType.Block)
make('CastleCampfireStone',Vector3.new(2,1,2),CFrame.new(-1,0.6,12),Enum.Material.Rock,Color3.fromRGB(226,222,205),Enum.PartType.Block)
make('CastleCampfireLogWest',Vector3.new(4,0.7,0.7),CFrame.new(-1,1,12),Enum.Material.Wood,Color3.fromRGB(218,205,170),Enum.PartType.Cylinder)
make('CastleCampfireLogEast',Vector3.new(4,0.7,0.7),CFrame.new(-1,1,12)*CFrame.Angles(0,math.rad(90),0),Enum.Material.Wood,Color3.fromRGB(218,205,170),Enum.PartType.Cylinder)
make('CastleCampfireFlame',Vector3.new(2.5,3.5,2.5),CFrame.new(-1,2,12),Enum.Material.Neon,Color3.fromRGB(255,137,45),Enum.PartType.Ball)
report('Added a paved courtyard, covered well, market stalls, and campfire with a glowing flame.')
end)
