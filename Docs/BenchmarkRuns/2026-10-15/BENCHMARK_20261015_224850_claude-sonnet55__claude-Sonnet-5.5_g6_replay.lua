-- Model-authored execute_lua calls in original order.
-- Model: claude/Sonnet 5.5; scenario: g6_free_build_vision; calls: 23
-- pcall keeps later calls running after a failed section, matching the benchmark.
local function run_chunk(index, body)
    local ok, err = pcall(body)
    if not ok then print('G6 chunk ' .. index .. ' failed: ' .. tostring(err)) end
end

run_chunk(1, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
P('Ground',V(128,1,128),C(0,-0.5,0),M.Grass,RGB(205,235,195))
P('CourtyardFloor',V(57,0.4,57),C(0,0.2,0),M.Pebble,RGB(228,218,198))
P('WallNorth',V(60,12,3),C(0,6,-30),M.Cobblestone,W)
P('WallEast',V(3,12,57),C(30,6,0),M.Cobblestone,W)
P('WallWest',V(3,12,57),C(-30,6,0),M.Cobblestone,W)
P('WallSouthWest',V(25,12,3),C(-17.5,6,30),M.Cobblestone,W)
P('WallSouthEast',V(25,12,3),C(17.5,6,30),M.Cobblestone,W)
P('GateLintel',V(10,4,3),C(0,10,30),M.Limestone,W)
P('GateThreshold',V(10,0.3,5),C(0,0.15,30),M.Pavement,RGB(226,224,214))
for i=1,5 do P('GatePortcullis'..i,V(4.4,0.5,0.5),C(-4+(i-1)*2,6.2,30)*CFrame.Angles(0,0,math.rad(90)),M.Metal,RGB(200,200,205),S.Cylinder) end
end)

run_chunk(2, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
local corners={{'NE',30,-30,M.Slate},{'NW',-30,-30,M.ClayRoofTiles},{'SE',30,30,M.ClayRoofTiles},{'SW',-30,30,M.Slate}}
for _,t in ipairs(corners) do
 P('Tower'..t[1],V(19,10,10),C(t[2],9.5,t[3])*CFrame.Angles(0,0,R),M.Limestone,W,S.Cylinder)
 P('TowerDeck'..t[1],V(1.5,11.5,11.5),C(t[2],19.75,t[3])*CFrame.Angles(0,0,R),M.Granite,W,S.Cylinder)
 P('TowerRoof'..t[1],V(9,9,9),C(t[2],24.6,t[3]),t[4],W,S.Ball)
end
local gates={{'GateW',-9},{'GateE',9}}
for _,g in ipairs(gates) do
 P('Tower'..g[1],V(18,8,8),C(g[2],9,30)*CFrame.Angles(0,0,R),M.Sandstone,W,S.Cylinder)
 P('TowerDeck'..g[1],V(1.2,9.2,9.2),C(g[2],18.6,30)*CFrame.Angles(0,0,R),M.Granite,W,S.Cylinder)
 P('TowerRoof'..g[1],V(7,7,7),C(g[2],22.4,30),M.ClayRoofTiles,W,S.Ball)
end
end)

run_chunk(3, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
P('KeepBody',V(18,26,18),C(-6,13,-8),M.Brick,W)
P('KeepRoofA',V(20,8,10),C(-6,30,-13),M.RoofShingles,W,S.Wedge)
P('KeepRoofB',V(20,8,10),C(-6,30,-3)*CFrame.Angles(0,math.rad(180),0),M.RoofShingles,W,S.Wedge)
P('KeepDoor',V(4,6,0.6),C(-6,3,1.3),M.Wood,RGB(200,170,130))
for i=1,3 do P('KeepWindow'..i,V(2,3.5,0.4),C(-11+(i-1)*5,15,1.2),M.Glass,RGB(140,190,240)) end
local tur={{'NE',3,-17},{'NW',-15,-17},{'SE',3,1},{'SW',-15,1}}
for _,t in ipairs(tur) do
 P('KeepTurret'..t[1],V(29,3.5,3.5),C(t[2],14.5,t[3])*CFrame.Angles(0,0,R),M.Limestone,W,S.Cylinder)
 P('KeepFinial'..t[1],V(3.5,3.5,3.5),C(t[2],30.55,t[3]),M.Metal,RGB(235,215,120),S.Ball)
end
P('KeepFlagPole',V(8,0.4,0.4),C(-6,38,-8)*CFrame.Angles(0,0,R),M.Metal,RGB(230,230,235),S.Cylinder)
P('KeepFlag',V(0.2,2,3.5),C(-6,40,-9.9),M.Fabric,RGB(200,30,40))
end)

run_chunk(4, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
local wb=RGB(90,150,210)
local function mo(n,s,cf) local p=P(n,s,cf,M.Glass,wb) p.Transparency=0.35 end
mo('MoatNorth',V(86,0.3,5),C(0,0.15,-38.5))
mo('MoatWest',V(5,0.3,72),C(-38.5,0.15,0))
mo('MoatEast',V(5,0.3,72),C(38.5,0.15,0))
mo('MoatSouthWest',V(38,0.3,5),C(-24,0.15,38.5))
mo('MoatSouthEast',V(38,0.3,5),C(24,0.15,38.5))
P('Drawbridge',V(7,0.6,6),C(0,0.3,38.5),M.WoodPlanks,W)
P('DrawbridgeRailW',V(0.4,1,6),C(-3.3,1.1,38.5),M.Wood,W)
P('DrawbridgeRailE',V(0.4,1,6),C(3.3,1.1,38.5),M.Wood,W)
local th=math.atan2(7.4,9.5)
local d=math.sqrt(7.4*7.4+9.5*9.5)
P('DrawbridgeChainW',V(d,0.3,0.3),C(-3,4.3,36.25)*CFrame.Angles(th,0,0)*CFrame.Angles(0,R,0),M.Metal,RGB(200,200,205),S.Cylinder)
P('DrawbridgeChainE',V(d,0.3,0.3),C(3,4.3,36.25)*CFrame.Angles(th,0,0)*CFrame.Angles(0,R,0),M.Metal,RGB(200,200,205),S.Cylinder)
P('RoadMain',V(8,0.3,23),C(0,0.15,52.5),M.Pavement,W)
P('RoadWestPath',V(22,0.25,5),C(-17,0.125,58)*CFrame.Angles(0,math.rad(-10),0),M.Cobblestone,W)
P('MudPatch',V(8,0.2,6),C(10,0.1,48),M.Mud,W)
P('SandBank',V(14,0.2,7),C(-14,0.1,47),M.Sand,W)
for _,sx in ipairs({-1,1}) do
 local t=(sx<0) and 'W' or 'E'
 P('RoadLampPost'..t,V(4,0.4,0.4),C(sx*5.5,2,46)*CFrame.Angles(0,0,R),M.Metal,RGB(200,200,205),S.Cylinder)
 P('RoadLamp'..t,V(1,1,1),C(sx*5.5,4.5,46),M.Neon,RGB(255,220,120),S.Ball)
end
end)

run_chunk(5, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
for i=0,7 do P('MerlonNorth'..i,V(2.5,2,1.5),C(-21+i*6,13,-30),M.Granite,W) end
for i=0,3 do P('MerlonEast'..i,V(1.5,2,2.5),C(30,13,-18+i*9),M.Limestone,W) end
for i=0,3 do P('MerlonWest'..i,V(1.5,2,2.5),C(-30,13,-16+i*9),M.Limestone,W) end
end)

run_chunk(6, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
P('WellRing',V(2.5,5,5),C(14,1.65,12)*CFrame.Angles(0,0,R),M.Rock,W,S.Cylinder)
local w=P('WellWater',V(0.2,3.6,3.6),C(14,2.95,12)*CFrame.Angles(0,0,R),M.Glass,RGB(90,150,210),S.Cylinder)
w.Transparency=0.3
P('WellPostW',V(5,0.4,0.4),C(11.8,5.4,12)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
P('WellPostE',V(5,0.4,0.4),C(16.2,5.4,12)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
P('WellRoofA',V(5.5,2,3.2),C(14,8.9,10.4),M.RoofShingles,W,S.Wedge)
P('WellRoofB',V(5.5,2,3.2),C(14,8.9,13.6)*CFrame.Angles(0,math.rad(180),0),M.RoofShingles,W,S.Wedge)
local function stall(n,x,z,col)
 P(n..'Table',V(5,1.2,2.4),C(x,1.0,z),M.WoodPlanks,W)
 P(n..'PostL',V(5,0.3,0.3),C(x-2.2,2.9,z+1.2)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
 P(n..'PostR',V(5,0.3,0.3),C(x+2.2,2.9,z+1.2)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
 P(n..'Awning',V(6,1.6,4),C(x,6.2,z+0.4),M.Fabric,col,S.Wedge)
 P(n..'Goods',V(1.2,0.8,1.2),C(x-1,2.0,z),M.Cardboard,W)
end
stall('StallBaker',-20,12,RGB(200,50,50))
stall('StallMerchant',-5,17,RGB(50,90,190))
end)

run_chunk(7, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
P('CrateLarge',V(2,2,2),C(22,1.4,-20),M.Wood,W)
P('CrateMedium',V(1.6,1.6,1.6),C(24.1,1.2,-19.4),M.Cardboard,W)
P('CrateTop',V(1.4,1.4,1.4),C(22,3.1,-20)*CFrame.Angles(0,math.rad(25),0),M.Wood,W)
P('BarrelA',V(2.2,1.6,1.6),C(24,1.5,-23)*CFrame.Angles(0,0,R),M.WoodPlanks,W,S.Cylinder)
P('BarrelB',V(2.2,1.6,1.6),C(22.4,1.5,-23.6)*CFrame.Angles(0,0,R),M.WoodPlanks,W,S.Cylinder)
P('BarrelC',V(2.2,1.6,1.6),C(25.8,1.5,-22)*CFrame.Angles(0,0,R),M.CorrodedMetal,W,S.Cylinder)
P('CampfireBase',V(0.4,2.6,2.6),C(-22,0.6,-10)*CFrame.Angles(0,0,R),M.CrackedLava,W,S.Cylinder)
P('CampfireFlame',V(1.2,1.2,1.2),C(-22,1.3,-10),M.Neon,RGB(255,150,40),S.Ball)
P('CampfireLogA',V(3,0.4,0.4),C(-22,0.8,-10)*CFrame.Angles(0,math.rad(30),0),M.Wood,W,S.Cylinder)
P('CampfireLogB',V(3,0.4,0.4),C(-22,0.8,-10)*CFrame.Angles(0,math.rad(-30),0),M.Wood,W,S.Cylinder)
P('BenchTop',V(4,0.4,1),C(-26,1.4,-6),M.WoodPlanks,W)
P('BenchLegW',V(0.4,0.8,0.8),C(-27.4,0.8,-6),M.Wood,W)
P('BenchLegE',V(0.4,0.8,0.8),C(-24.6,0.8,-6),M.Wood,W)
P('KeepSteps',V(6,0.4,2),C(-6,0.6,2.2),M.Marble,W)
P('KeepCarpet',V(4,0.1,3),C(-6,0.85,2.2),M.Carpet,RGB(160,30,40))
end)

run_chunk(8, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
P('BarracksBody',V(10,6,12),C(23,3.4,-8),M.Plaster,W)
P('BarracksRoofA',V(10.6,3,6.6),C(23,7.9,-11.3),M.ClayRoofTiles,W,S.Wedge)
P('BarracksRoofB',V(10.6,3,6.6),C(23,7.9,-4.7)*CFrame.Angles(0,math.rad(180),0),M.ClayRoofTiles,W,S.Wedge)
P('BarracksDoor',V(0.4,4,2.4),C(17.8,2.4,-8),M.Wood,RGB(200,170,130))
P('BarracksChimney',V(6,1.4,1.4),C(25.5,9.4,-8)*CFrame.Angles(0,0,R),M.Brick,W,S.Cylinder)
P('SmithyBody',V(9,4.5,6),C(-18,2.65,22),M.WoodPlanks,W)
P('SmithyRoofA',V(10,2.5,3.5),C(-18,6.15,20.25),M.RoofShingles,W,S.Wedge)
P('SmithyRoofB',V(10,2.5,3.5),C(-18,6.15,23.75)*CFrame.Angles(0,math.rad(180),0),M.RoofShingles,W,S.Wedge)
P('SmithyDoor',V(2.4,3.4,0.4),C(-18,2.1,25.2),M.Wood,RGB(200,170,130))
P('SmithyChimney',V(5,1.4,1.4),C(-21.2,7.4,22)*CFrame.Angles(0,0,R),M.Brick,W,S.Cylinder)
P('SmithyStump',V(0.8,1.2,1.2),C(-18,0.8,27)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
P('SmithyAnvil',V(1.2,0.8,0.6),C(-18,1.6,27),M.Metal,RGB(200,200,205))
end)

run_chunk(9, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
local function T(n,x,z,h,r)
 P('Tree'..n..'Trunk',V(h,1,1),C(x,h/2,z)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
 P('Tree'..n..'Crown',V(2*r,2*r,2*r),C(x,h+r*0.6,z),M.LeafyGrass,RGB(200,240,180),S.Ball)
end
T('A',-52,-30,6,4.5)
T('B',-30,-52,7,5)
T('C',-8,-50,5,4)
T('D',14,-55,8,5.5)
T('E',38,-52,6,4.5)
T('F',52,-20,7,5)
T('G',55,10,5,4)
T('H',-55,5,6,4.5)
P('BoulderRock',V(6,6,6),C(-40,3,-44),M.Rock,W,S.Ball)
P('BoulderGranite',V(5,3.5,4.5),C(25,1.75,-46)*CFrame.Angles(0,math.rad(30),0),M.Granite,W)
P('BoulderBasalt',V(3,2.5,3.5),C(46,1.25,-30)*CFrame.Angles(0,math.rad(-20),0),M.Basalt,W)
end)

run_chunk(10, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
P('FarmhouseBody',V(10,5,8),C(-50,2.5,50),M.Plaster,W)
P('FarmhouseRoofA',V(10.6,3,4.6),C(-50,6.5,47.7),M.RoofShingles,W,S.Wedge)
P('FarmhouseRoofB',V(10.6,3,4.6),C(-50,6.5,52.3)*CFrame.Angles(0,math.rad(180),0),M.RoofShingles,W,S.Wedge)
P('FarmhouseDoor',V(2,3.5,0.4),C(-50,1.75,54.2),M.Wood,RGB(200,170,130))
P('FarmhouseWindow',V(2,1.6,0.4),C(-53,3,54.2),M.Glass,RGB(140,190,240))
P('FarmhouseChimney',V(5,1.4,1.4),C(-47,7,50)*CFrame.Angles(0,0,R),M.Brick,W,S.Cylinder)
P('GardenPlot',V(10,0.3,8),C(-33,0.15,53),M.Ground,W)
for i=0,2 do P('GardenRow'..i,V(8,0.8,1),C(-33,0.7,50.5+i*2.5),M.LeafyGrass,RGB(200,240,180)) end
P('GardenFenceRail',V(10.6,0.5,0.3),C(-33,1.1,57.2),M.WoodPlanks,W)
for i=0,2 do P('GardenFencePost'..i,V(1.8,0.4,0.4),C(-38+i*5,0.9,57.2)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder) end
end)

run_chunk(11, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
local function tent(n,x,z,yaw,col)
 P(n..'A',V(7,4.5,3.5),C(x,2.25,z)*CFrame.Angles(0,yaw,0)*CFrame.new(0,0,-1.75),M.Fabric,col,S.Wedge)
 P(n..'B',V(7,4.5,3.5),C(x,2.25,z)*CFrame.Angles(0,yaw+math.pi,0)*CFrame.new(0,0,-1.75),M.Fabric,col,S.Wedge)
end
tent('TentRed',45,48,0,RGB(200,60,50))
tent('TentBlue',54,38,math.rad(25),RGB(60,100,190))
P('Haystack',V(4,4,4),C(30,2,52),M.Sand,RGB(240,220,150),S.Ball)
P('HutBody',V(6,4,6),C(45,2,-45),M.WoodPlanks,W)
P('HutRoofSW',V(3,3,3),C(43.5,5.5,-46.5),M.RoofShingles,W,S.CornerWedge)
P('HutRoofSE',V(3,3,3),C(46.5,5.5,-46.5)*CFrame.Angles(0,R,0),M.RoofShingles,W,S.CornerWedge)
P('HutRoofNE',V(3,3,3),C(46.5,5.5,-43.5)*CFrame.Angles(0,math.pi,0),M.RoofShingles,W,S.CornerWedge)
P('HutRoofNW',V(3,3,3),C(43.5,5.5,-43.5)*CFrame.Angles(0,-R,0),M.RoofShingles,W,S.CornerWedge)
P('HutDoor',V(1.8,3,0.4),C(45,1.5,-41.8),M.Wood,RGB(200,170,130))
P('HillRock',V(10,10,10),C(-45,5,-50),M.Slate,W,S.Ball)
P('HillSnowCap',V(5,5,5),C(-45,9.5,-50),M.Snow,W,S.Ball)
local pnd=P('IcePond',V(10,0.2,8),C(52,0.1,-5),M.Ice,RGB(190,225,245))
pnd.Transparency=0.2
end)

run_chunk(12, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
for i,x in ipairs({-22,-19,-16,16,19,22}) do P('MerlonSouth'..i,V(2.5,2,1.5),C(x,13,30),M.Granite,W) end
P('StableBody',V(9,4.5,7),C(22,2.65,22),M.WoodPlanks,W)
P('StableRoofA',V(10,2.5,3.75),C(22,6.15,20.125),M.RoofShingles,W,S.Wedge)
P('StableRoofB',V(10,2.5,3.75),C(22,6.15,23.875)*CFrame.Angles(0,math.rad(180),0),M.RoofShingles,W,S.Wedge)
P('StableDoor',V(3,3.4,0.4),C(22,2.1,25.7),M.Wood,RGB(200,170,130))
P('StableHay',V(2,2,2),C(27.5,1.4,22),M.Sand,RGB(240,220,150),S.Ball)
P('StableTrough',V(4,0.8,1),C(22,0.8,27),M.DiamondPlate,W)
P('WellPlaza',V(8,0.15,8),C(14,0.475,12),M.CeramicTiles,RGB(230,225,210))
P('SmithyYard',V(8,0.15,3),C(-18,0.475,26.5),M.Asphalt,RGB(200,200,200))
P('BannerGateW',V(2,5,0.2),C(-20,7.5,31.6),M.SmoothPlastic,RGB(190,30,40))
P('BannerGateE',V(2,5,0.2),C(20,7.5,31.6),M.SmoothPlastic,RGB(40,70,180))
P('BridgePierW',V(1.5,1.2,1.5),C(-5,0.6,38.5),M.Concrete,W)
P('BridgePierE',V(1.5,1.2,1.5),C(5,0.6,38.5),M.Concrete,W)
end)

run_chunk(13, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
P('WindmillTower',V(14,6,6),C(-48,7,22)*CFrame.Angles(0,0,R),M.Brick,W,S.Cylinder)
P('WindmillCap',V(6.4,6.4,6.4),C(-48,16.2,22),M.RoofShingles,W,S.Ball)
P('WindmillDoor',V(1.6,3,0.4),C(-48,1.5,25.1),M.Wood,RGB(200,170,130))
P('WindmillHub',V(1.2,1.2,1.2),C(-48,12,24.9),M.Metal,RGB(200,200,205),S.Ball)
P('WindmillArmV',V(0.5,12,0.3),C(-48,12,25.5),M.Wood,W)
P('WindmillArmH',V(12,0.5,0.3),C(-48,12,25.8),M.Wood,W)
P('WindmillSailA',V(2.5,4,0.1),C(-49.5,15,25.5),M.Fabric,W)
P('WindmillSailB',V(2.5,4,0.1),C(-46.5,9,25.5),M.Fabric,W)
P('RoadFenceRailW',V(0.3,0.3,12),C(-6.5,1.6,56),M.WoodPlanks,W)
P('RoadFenceRailE',V(0.3,0.3,12),C(6.5,1.6,56),M.WoodPlanks,W)
local k=0
for _,x in ipairs({-6.5,6.5}) do for _,z in ipairs({50.5,56,61.5}) do k=k+1 P('RoadFencePost'..k,V(2,0.4,0.4),C(x,1,z)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder) end end
P('SignPost',V(4,0.4,0.4),C(9,2,52)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
P('SignBoard',V(3,1.2,0.2),C(9,3.4,52.3),M.Leather,RGB(210,170,120))
P('KeepFlagFinial',V(1.2,1.2,1.2),C(-6,42.6,-8),M.Foil,RGB(240,230,150),S.Ball)
end)

run_chunk(14, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
local function T(n,x,z,h,r)
 P('Tree'..n..'Trunk',V(h,1,1),C(x,h/2,z)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
 P('Tree'..n..'Crown',V(2*r,2*r,2*r),C(x,h+r*0.6,z),M.LeafyGrass,RGB(200,240,180),S.Ball)
end
T('I',-20,-58,6,4)
T('J',28,-58,7,4.5)
T('K',58,-45,6,4.5)
T('L',-58,-10,7,5)
T('M',-58,30,6,4.5)
T('N',57,24,5,4)
local bushes={{33.5,10},{33.5,-6},{-33.5,-14},{-33.5,8},{-33.5,-2}}
for i,b in ipairs(bushes) do P('Bush'..i,V(2.5,2.5,2.5),C(b[1],1.25,b[2]),M.Grass,RGB(200,240,180),S.Ball) end
P('RockPileA',V(3,3,3),C(-36,1.5,-56),M.Rock,W,S.Ball)
P('RockPileB',V(4,2,3),C(36,1,-56)*CFrame.Angles(0,math.rad(40),0),M.Slate,W)
end)

run_chunk(15, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
P('ChapelBody',V(8,6,10),C(-20,3.4,-21),M.Limestone,W)
P('ChapelRoofA',V(9,3,5.2),C(-20,7.9,-23.4),M.Slate,W,S.Wedge)
P('ChapelRoofB',V(9,3,5.2),C(-20,7.9,-18.6)*CFrame.Angles(0,math.rad(180),0),M.Slate,W,S.Wedge)
P('ChapelDoor',V(2.4,4,0.4),C(-20,2.4,-15.8),M.Wood,RGB(200,170,130))
P('ChapelWindowW',V(1.4,2.4,0.4),C(-22.8,4.5,-15.8),M.Glass,RGB(200,80,160))
P('ChapelWindowE',V(1.4,2.4,0.4),C(-17.2,4.5,-15.8),M.Glass,RGB(70,120,230))
P('ChapelSteeple',V(12,3,3),C(-25.6,6,-18)*CFrame.Angles(0,0,R),M.Granite,W,S.Cylinder)
P('ChapelSteepleCap',V(3.4,3.4,3.4),C(-25.6,13.2,-18),M.ClayRoofTiles,W,S.Ball)
P('ChapelCrossV',V(0.3,2,0.3),C(-25.6,15.9,-18),M.Metal,RGB(235,215,120))
P('ChapelCrossH',V(1.2,0.3,0.3),C(-25.6,16.4,-18),M.Metal,RGB(235,215,120))
P('DummyPost',V(4,0.5,0.5),C(-27,2,8)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
P('DummyHead',V(1.2,1.2,1.2),C(-27,4.6,8),M.Fabric,RGB(230,210,160),S.Ball)
P('DummyArm',V(0.4,0.4,3),C(-27,3.4,8),M.Wood,W)
P('TargetStand',V(4,0.4,0.4),C(-27,2,14)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
P('TargetDisc',V(0.4,3,3),C(-26.6,3.8,14),M.Fabric,RGB(220,60,50),S.Cylinder)
end)

run_chunk(16, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
P('WatchtowerShaft',V(14,5,5),C(56,7,-32)*CFrame.Angles(0,0,R),M.Brick,W,S.Cylinder)
P('WatchtowerDeck',V(1,7,7),C(56,14.5,-32)*CFrame.Angles(0,0,R),M.WoodPlanks,W,S.Cylinder)
P('WatchtowerRoof',V(6,6,6),C(56,17.5,-32),M.ClayRoofTiles,W,S.Ball)
P('WatchtowerDoor',V(1.6,3,0.4),C(56,1.5,-29.4),M.Wood,RGB(200,170,130))
P('WatchtowerTorch',V(0.9,0.9,0.9),C(59.3,15.8,-32),M.Neon,RGB(255,170,60),S.Ball)
P('CartBody',V(4,1,2.5),C(14,1.6,56),M.WoodPlanks,W)
for i,w in ipairs({{12.6,57.4},{15.4,57.4},{12.6,54.6},{15.4,54.6}}) do P('CartWheel'..i,V(0.3,2,2),C(w[1],1,w[2])*CFrame.Angles(0,R,0),M.Wood,W,S.Cylinder) end
P('CartHay',V(2.4,1.4,1.8),C(14,2.8,56),M.Sand,RGB(240,220,150))
P('PenMud',V(8,0.2,6),C(-38,0.1,44),M.Mud,W)
P('PenPigA',V(1.6,1.6,1.6),C(-39.5,0.8,44),M.SmoothPlastic,RGB(245,170,180),S.Ball)
P('PenPigB',V(1.4,1.4,1.4),C(-36.5,0.7,45),M.SmoothPlastic,RGB(240,160,170),S.Ball)
P('PenRailN',V(8.4,0.4,0.3),C(-38,1.3,41),M.WoodPlanks,W)
P('PenRailS',V(8.4,0.4,0.3),C(-38,1.3,47),M.WoodPlanks,W)
P('StandingStoneA',V(1.8,5,1.4),C(40,2.5,-12)*CFrame.Angles(0,math.rad(15),0),M.Granite,W)
P('StandingStoneB',V(1.6,4,1.4),C(43,2,-14)*CFrame.Angles(0,math.rad(-25),0),M.Basalt,W)
P('StandingStoneC',V(1.8,4.4,1.3),C(37,2.2,-14)*CFrame.Angles(0,math.rad(40),0),M.Rock,W)
end)

run_chunk(17, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
local torches={{'A',10,-25},{'B',-8,-25},{'C',3,24},{'D',-3,24}}
for _,t in ipairs(torches) do
 P('TorchPost'..t[1],V(4,0.3,0.3),C(t[2],2,t[3])*CFrame.Angles(0,0,R),M.Metal,RGB(200,200,205),S.Cylinder)
 P('TorchFlame'..t[1],V(0.9,0.9,0.9),C(t[2],4.2,t[3]),M.Neon,RGB(255,170,60),S.Ball)
end
P('GraveMound',V(7,0.6,9),C(-46,0.3,-16),M.Ground,W)
for i=0,3 do P('GraveStone'..i,V(1.4,2.2,0.5),C(-48.5+i*1.9,1.4,-14.5+(i%2)*3)*CFrame.Angles(0,math.rad(-8+i*6),0),M.Granite,W) end
P('GraveFenceRailN',V(8.4,0.4,0.3),C(-46,1.4,-20.7),M.Metal,RGB(200,200,205))
P('GraveFenceRailS',V(8.4,0.4,0.3),C(-46,1.4,-11.3),M.Metal,RGB(200,200,205))
P('GraveCross',V(0.4,3.4,0.4),C(-46,2.3,-17.5),M.Marble,W)
P('GraveCrossBar',V(1.8,0.4,0.4),C(-46,3.1,-17.5),M.Marble,W)
end)

run_chunk(18, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
local function T(n,x,z,h,r)
 P('Tree'..n..'Trunk',V(h,1,1),C(x,h/2,z)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
 P('Tree'..n..'Crown',V(2*r,2*r,2*r),C(x,h+r*0.6,z),M.LeafyGrass,RGB(200,240,180),S.Ball)
end
T('O',2,-60,7,4.5)
T('P',-44,-36,6,4)
T('Q',48,58,6,4.5)
P('HayBaleA',V(2.4,1.6,1.6),C(40,0.8,56),M.Sand,RGB(240,220,150))
P('HayBaleB',V(2.4,1.6,1.6),C(42.6,0.8,57)*CFrame.Angles(0,math.rad(20),0),M.Sand,RGB(240,220,150))
P('FlowerRed',V(0.8,0.8,0.8),C(-8,0.4,64-8),M.Plastic,RGB(220,40,60),S.Ball)
P('FlowerYellow',V(0.8,0.8,0.8),C(-10,0.4,52),M.Plastic,RGB(250,210,40),S.Ball)
P('FlowerPurple',V(0.8,0.8,0.8),C(10,0.4,60),M.Plastic,RGB(150,70,200),S.Ball)
P('PondStoneA',V(1.6,1,1.6),C(46.5,0.5,-5),M.Pebble,W,S.Ball)
P('PondStoneB',V(2,0.9,1.4),C(57.5,0.45,-6)*CFrame.Angles(0,math.rad(35),0),M.Limestone,W)
P('FirewoodStack',V(3,1.4,1.2),C(-27,0.7,-12),M.Wood,W)
P('WaterTrough',V(3,0.9,1),C(-12,0.5,22),M.Concrete,W)
end)

run_chunk(19, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
local flags={{'NE',30,-30,RGB(200,30,40)},{'NW',-30,-30,RGB(40,70,180)},{'SE',30,30,RGB(230,190,40)},{'SW',-30,30,RGB(40,150,70)}}
for _,f in ipairs(flags) do
 P('TowerFlagPole'..f[1],V(5,0.3,0.3),C(f[2],31.5,f[3])*CFrame.Angles(0,0,R),M.Metal,RGB(230,230,235),S.Cylinder)
 P('TowerFlag'..f[1],V(1.8,1.2,0.15),C(f[2]+1,33.4,f[3]),M.Fabric,f[4])
end
P('StallGrocerTable',V(5,1.2,2.4),C(7,1.0,6),M.WoodPlanks,W)
P('StallGrocerPostL',V(5,0.3,0.3),C(4.8,2.9,7.2)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
P('StallGrocerPostR',V(5,0.3,0.3),C(9.2,2.9,7.2)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
P('StallGrocerAwning',V(6,1.6,4),C(7,6.2,6.4),M.Fabric,RGB(60,160,80),S.Wedge)
P('StallGrocerGoods',V(1.2,0.8,1.2),C(6,2.0,6),M.Leather,RGB(210,170,120))
P('StallGrocerSack',V(1.4,1.4,1.4),C(8.3,1.4,6.2),M.Fabric,RGB(230,215,180),S.Ball)
end)

run_chunk(20, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
local function T(n,x,z,h,r)
 P('Tree'..n..'Trunk',V(h,1,1),C(x,h/2,z)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
 P('Tree'..n..'Crown',V(2*r,2*r,2*r),C(x,h+r*0.6,z),M.LeafyGrass,RGB(200,240,180),S.Ball)
end
T('R',-60,50,6,4)
T('S',60,50,7,4.5)
T('T',20,60,5,3.5)
P('OutWellRing',V(2,4.4,4.4),C(-58,1,-38)*CFrame.Angles(0,0,R),M.Brick,W,S.Cylinder)
P('OutWellWater',V(0.2,3,3),C(-58,2.05,-38)*CFrame.Angles(0,0,R),M.Glass,RGB(90,150,210),S.Cylinder)
P('CairnBase',V(5,2,4),C(48,1,-56)*CFrame.Angles(0,math.rad(15),0),M.Slate,W)
P('CairnTop',V(3,2,2.6),C(48,3,-56)*CFrame.Angles(0,math.rad(-20),0),M.Basalt,W)
P('CairnCap',V(1.8,1.8,1.8),C(48,4.7,-56),M.Rock,W,S.Ball)
P('OutCampLogA',V(3,0.6,0.6),C(-52,0.3,-56)*CFrame.Angles(0,math.rad(10),0),M.Wood,W,S.Cylinder)
P('OutCampFire',V(1,1,1),C(-54,0.6,-54),M.Neon,RGB(255,150,40),S.Ball)
P('OutCampTent',V(5,3.5,3),C(-56,1.75,-48),M.Fabric,RGB(210,170,60),S.Wedge)
end)

run_chunk(21, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
P('WallStairsNorth',V(3,6,10),C(-12,3,-23.5)*CFrame.Angles(0,math.rad(180),0),M.Limestone,W,S.Wedge)
P('WallStairsLanding',V(3,6,3),C(-12,3,-27.5),M.Brick,W)
P('WallStairsEast',V(10,6,3),C(24,3,10.5)*CFrame.Angles(0,R,0),M.Granite,W,S.Wedge)
P('BarrelStallA',V(2.2,1.6,1.6),C(-16,1.5,15)*CFrame.Angles(0,0,R),M.WoodPlanks,W,S.Cylinder)
P('BarrelStallB',V(2.2,1.6,1.6),C(-14,1.5,16)*CFrame.Angles(0,0,R),M.CorrodedMetal,W,S.Cylinder)
P('CrateStall',V(1.8,1.8,1.8),C(-8,1,20),M.Wood,W)
P('PlanterKeepW',V(2,1,2),C(-10,0.5,3),M.Concrete,W)
P('PlanterShrubW',V(1.8,1.8,1.8),C(-10,1.7,3),M.Grass,RGB(200,240,180),S.Ball)
P('PlanterKeepE',V(2,1,2),C(-2,0.5,3),M.Concrete,W)
P('PlanterShrubE',V(1.8,1.8,1.8),C(-2,1.7,3),M.Grass,RGB(200,240,180),S.Ball)
P('RoadMilestone',V(1.2,2,0.8),C(-5.5,1,64),M.Limestone,W)
P('RoadBoulderEast',V(3,2.4,3),C(8,1.2,62),M.Rock,W,S.Ball)
end)

run_chunk(22, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
P('BenchCourtTop',V(4,0.4,1),C(2,1.4,-20),M.WoodPlanks,W)
P('BenchCourtLegW',V(0.4,0.8,0.8),C(0.6,0.8,-20),M.Wood,W)
P('BenchCourtLegE',V(0.4,0.8,0.8),C(3.4,0.8,-20),M.Wood,W)
P('CrateBarracks',V(1.8,1.8,1.8),C(16,1.1,-14),M.Wood,W)
P('BarrelBarracks',V(2.2,1.6,1.6),C(16.5,1.5,-16.5)*CFrame.Angles(0,0,R),M.WoodPlanks,W,S.Cylinder)
P('WeaponRack',V(4,0.4,0.4),C(26,2.6,-1)*CFrame.Angles(0,0,0),M.Metal,RGB(200,200,205))
P('WeaponRackPostA',V(2.6,0.4,0.4),C(24.2,1.3,-1)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
P('WeaponRackPostB',V(2.6,0.4,0.4),C(27.8,1.3,-1)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
P('ShieldRed',V(0.3,1.8,1.8),C(25,1.6,-0.4),M.Fabric,RGB(200,40,50),S.Cylinder)
P('ShieldBlue',V(0.3,1.8,1.8),C(27,1.6,-0.4),M.Fabric,RGB(50,80,200),S.Cylinder)
P('FlowerBedE',V(5,0.6,1.4),C(-6,0.3,-25.8),M.Ground,W)
P('FlowerBedShrub',V(1.6,1.6,1.6),C(-6,1.3,-25.8),M.LeafyGrass,RGB(200,240,180),S.Ball)
end)

run_chunk(23, function()
local V,C,RGB,M,S=Vector3.new,CFrame.new,Color3.fromRGB,Enum.Material,Enum.PartType
local function P(n,s,cf,m,c,sh) local p=Instance.new('Part') p.Name='Castle'..n p.Size=s p.CFrame=cf p.Material=m p.Color=c p.Shape=sh or S.Block p.Anchored=true p.Parent=workspace return p end
local W=RGB(226,224,214)
local R=math.rad(90)
local function T(n,x,z,h,r)
 P('Tree'..n..'Trunk',V(h,1,1),C(x,h/2,z)*CFrame.Angles(0,0,R),M.Wood,W,S.Cylinder)
 P('Tree'..n..'Crown',V(2*r,2*r,2*r),C(x,h+r*0.6,z),M.LeafyGrass,RGB(200,240,180),S.Ball)
end
T('U',35,62,6,4)
T('V',-62,-25,7,4.5)
for _,b in ipairs({{'W',-8},{'E',8}}) do
 P('BrazierStand'..b[1],V(2.4,0.4,0.4),C(b[2],1.2,42)*CFrame.Angles(0,0,R),M.Metal,RGB(200,200,205),S.Cylinder)
 P('BrazierBowl'..b[1],V(0.8,1.6,1.6),C(b[2],2.6,42)*CFrame.Angles(0,0,R),M.CorrodedMetal,W,S.Cylinder)
 P('BrazierFlame'..b[1],V(1.1,1.1,1.1),C(b[2],3.5,42),M.Neon,RGB(255,150,40),S.Ball)
end
P('RockPileC',V(3,3,3),C(-14,1.5,-62),M.Rock,W,S.Ball)
P('RockPileD',V(4,2.2,3),C(18,1.1,-62)*CFrame.Angles(0,math.rad(25),0),M.Granite,W)
end)
