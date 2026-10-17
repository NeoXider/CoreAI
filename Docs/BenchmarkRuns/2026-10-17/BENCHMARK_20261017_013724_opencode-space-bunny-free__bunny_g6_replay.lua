-- Model-authored execute_lua calls in original order.
-- Model: bunny; scenario: g6_free_build; calls: 42
-- pcall keeps later calls running after a failed section, matching the benchmark.
local function run_chunk(index, body)
    local ok, err = pcall(body)
    if not ok then print('G6 chunk ' .. index .. ' failed: ' .. tostring(err)) end
end

run_chunk(1, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
P('CastleGround',Vector3.new(132,2,132),CFrame.new(0,-1,0),Enum.Material.Grass,Color3.fromRGB(196,214,168))
P('CastleGroundPlateau',Vector3.new(96,4,96),CFrame.new(0,0,0),Enum.Material.Rock,Color3.fromRGB(206,198,182))
P('CastleMoatNorth',Vector3.new(120,2,9),CFrame.new(0,1.6,-40),Enum.Material.Water,Color3.fromRGB(120,175,205))
P('CastleMoatWest',Vector3.new(9,2,86),CFrame.new(-40,1.6,0),Enum.Material.Water,Color3.fromRGB(120,175,205))
P('CastleMoatSouth',Vector3.new(120,2,9),CFrame.new(0,1.6,40),Enum.Material.Water,Color3.fromRGB(120,175,205))
P('CastleMoatEast',Vector3.new(9,2,86),CFrame.new(40,1.6,0),Enum.Material.Water,Color3.fromRGB(120,175,205))
report('ground+moat')
end)

run_chunk(2, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
-- curtain walls (h=12, thick 3), footprint encloses courtyard
P('CastleWallNorthW',Vector3.new(34,12,3),CFrame.new(-17,6,-27),Enum.Material.Cobblestone,Color3.fromRGB(226,224,214))
P('CastleWallNorthE',Vector3.new(30,12,3),CFrame.new(19,6,-27),Enum.Material.Cobblestone,Color3.fromRGB(226,224,214))
P('CastleWallSouthW',Vector3.new(30,12,3),CFrame.new(-15,6,27),Enum.Material.Cobblestone,Color3.fromRGB(226,224,214))
P('CastleWallSouthE',Vector3.new(28,12,3),CFrame.new(17,6,27),Enum.Material.Cobblestone,Color3.fromRGB(226,224,214))
P('CastleWallWest',Vector3.new(3,12,40),CFrame.new(-27,6,0),Enum.Material.Cobblestone,Color3.fromRGB(226,224,214))
P('CastleWallEast',Vector3.new(3,12,40),CFrame.new(27,6,0),Enum.Material.Cobblestone,Color3.fromRGB(226,224,214))
report('curtain')
end)

run_chunk(3, function()
for _,n in ipairs({'CastleWallNorthW','CastleWallNorthE','CastleWallSouthW','CastleWallSouthE','CastleWallWest','CastleWallEast'}) do local q=workspace:FindFirstChild(n) if q then q:Destroy() end end
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(226,224,214)
local DK=Color3.fromRGB(206,202,192)
P('CastleWallNorthWest',Vector3.new(29,12,3),CFrame.new(-19.5,6,-27),Enum.Material.Cobblestone,ST)
P('CastleWallNorthEast',Vector3.new(29,12,3),CFrame.new(19.5,6,-27),Enum.Material.Cobblestone,ST)
P('CastleWallSouthWest',Vector3.new(33,12,3),CFrame.new(-17.5,6,27),Enum.Material.Cobblestone,ST)
P('CastleWallSouthEast',Vector3.new(35,12,3),CFrame.new(16.5,6,27),Enum.Material.Cobblestone,ST)
P('CastleWallWest',Vector3.new(3,12,51),CFrame.new(-31.5,6,0),Enum.Material.Cobblestone,ST)
P('CastleWallEast',Vector3.new(3,12,51),CFrame.new(31.5,6,0),Enum.Material.Cobblestone,ST)
P('CastleParapetNorth',Vector3.new(68,1.6,3),CFrame.new(0,12.8,-27),Enum.Material.Limestone,DK)
P('CastleParapetSouth',Vector3.new(68,1.6,3),CFrame.new(0,12.8,27),Enum.Material.Limestone,DK)
P('CastleParapetWest',Vector3.new(3,1.6,51),CFrame.new(-31.5,12.8,0),Enum.Material.Limestone,DK)
P('CastleParapetEast',Vector3.new(3,1.6,51),CFrame.new(31.5,12.8,0),Enum.Material.Limestone,DK)
P('CastleGateLintel',Vector3.new(10,4,3),CFrame.new(0,10,-27),Enum.Material.Granite,DK)
P('CastleGateCorbelWest',Vector3.new(3,2,3),CFrame.new(-3,9,-27),Enum.Material.Granite,DK)
P('CastleGateCorbelEast',Vector3.new(3,2,3),CFrame.new(3,9,-27),Enum.Material.Granite,DK)
P('CastleGateJambWest',Vector3.new(2,8,3.6),CFrame.new(-5.5,4,-27),Enum.Material.Granite,DK)
P('CastleGateJambEast',Vector3.new(2,8,3.6),CFrame.new(5.5,4,-27),Enum.Material.Granite,DK)
report('curtain walls + gate opening')
end)

run_chunk(4, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(226,224,214)
local DK=Color3.fromRGB(212,208,198)
local spec={
 {'CastleTowerNW',-31.5,-27,12,20},
 {'CastleTowerNE',31.5,-27,12,20},
 {'CastleTowerSW',-31.5,27,12,20},
 {'CastleTowerSE',31.5,27,12,20},
 {'CastleTowerGateWest',-11,-27,11,22},
 {'CastleTowerGateEast',11,-27,11,22}}
for i=1,6 do
 local n,x,z,d,h=spec[i][1],spec[i][2],spec[i][3],spec[i][4],spec[i][5]
 P(n..'Drum',Vector3.new(h,d,d),CFrame.new(x,h/2,z)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Granite,ST)
 P(n..'Corbel',Vector3.new(2.2,d+2.4,d+2.4),CFrame.new(x,h-1.1,z)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Limestone,DK)
 P(n..'Battlement',Vector3.new(1.6,d+0.6,d+0.6),CFrame.new(x,h+0.8,z)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Limestone,DK)
end
report('six towers')
end)

run_chunk(5, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,226,216)
local DK=Color3.fromRGB(214,210,200)
-- keep in courtyard, 10+ studs in from walls
P('CastleKeepBase',Vector3.new(26,3,22),CFrame.new(-2,1.5,3),Enum.Material.Granite,ST)
P('CastleKeepBody',Vector3.new(22,14,18),CFrame.new(-2,10,3),Enum.Material.Cobblestone,ST)
P('CastleKeepStringCourse',Vector3.new(23,1.2,19),CFrame.new(-2,17.6,3),Enum.Material.Limestone,DK)
P('CastleKeepUpper',Vector3.new(18,9,14),CFrame.new(-2,22.7,3),Enum.Material.Cobblestone,ST)
P('CastleKeepParapet',Vector3.new(19,1.5,15),CFrame.new(-2,27.95,3),Enum.Material.Limestone,DK)
P('CastleKeepRoofRidge',Vector3.new(19,1.6,2),CFrame.new(-2,30.4,3),Enum.Material.Slate,Color3.fromRGB(196,200,210))
P('CastleKeepRoofSouth',Vector3.new(20,1.4,9),CFrame.new(-2,29.6,9.2)*CFrame.Angles(math.rad(28),0,0),Enum.Material.Slate,Color3.fromRGB(196,200,210))
P('CastleKeepRoofNorth',Vector3.new(20,1.4,9),CFrame.new(-2,29.6,-3.2)*CFrame.Angles(-math.rad(28),0,0),Enum.Material.Slate,Color3.fromRGB(196,200,210))
P('CastleKeepWindowA',Vector3.new(0.4,3,1.2),CFrame.new(-13.1,13,3),Enum.Material.Glass,Color3.fromRGB(120,170,190))
P('CastleKeepWindowB',Vector3.new(0.4,3,1.2),CFrame.new(9.1,13,3),Enum.Material.Glass,Color3.fromRGB(120,170,190))
P('CastleKeepWindowC',Vector3.new(1.2,3,0.4),CFrame.new(-2,13,12.1),Enum.Material.Glass,Color3.fromRGB(120,170,190))
P('CastleKeepWindowD',Vector3.new(1.2,3,0.4),CFrame.new(-2,13,-6.1),Enum.Material.Glass,Color3.fromRGB(120,170,190))
P('CastleKeepDoorArch',Vector3.new(3.4,4.4,0.6),CFrame.new(-2,5.2,12.4),Enum.Material.Wood,Color3.fromRGB(150,102,66))
P('CastleKeepStairStep1',Vector3.new(8,0.8,3),CFrame.new(-2,0.4,15.5),Enum.Material.Limestone,DK)
P('CastleKeepStairStep2',Vector3.new(7,0.8,2.4),CFrame.new(-2,1.2,14.2),Enum.Material.Limestone,DK)
P('CastleKeepBanner',Vector3.new(0.3,9,2.6),CFrame.new(7.3,17,12.3),Enum.Material.Fabric,Color3.fromRGB(168,38,42))
P('CastleKeepPennant',Vector3.new(0.25,3.2,4.2),CFrame.new(-2,33.4,3),Enum.Material.Fabric,Color3.fromRGB(40,72,160))
report('keep built')
end)

run_chunk(6, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(226,224,214)
local DK=Color3.fromRGB(210,206,196)
-- tower cone roofs: 4 corner quadrants per tower using CornerWedge
local t={{'NW',-31.5,-27},{'NE',31.5,-27},{'SW',-31.5,27},{'SE',31.5,27},{'GW',-11,-27},{'GE',11,-27}}
for i=1,6 do
 local base=t[i][1]; local x=t[i][2]; local z=t[i][3]
 local d=(base=='GW' or base=='GE') and 11 or 12
 local y=22
 for q=1,4 do
  local ax=(q==1 or q==4) and -1 or 1
  local az=(q<=2) and -1 or 1
  local cn=base..'RoofQ'..q
  P('Castle'..cn,Vector3.new(d*0.62,d*0.62,d*0.62),CFrame.new(x+ax*d*0.24,y+d*0.30,z+az*d*0.24)*CFrame.Angles(0,math.rad((q==1 and 0) or (q==2 and 90) or (q==3 and 180) or 270),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(222,164,140),Enum.PartType.CornerWedge)
 end
 P('Castle'..base..'Finial',Vector3.new(1.2,1.2,1.2),CFrame.new(x,y+d*0.86,z),Enum.Material.Gold,Color3.fromRGB(246,214,96),Enum.PartType.Ball)
end
report('tower roofs')
end)

run_chunk(7, function()
for i=1,4 do local q=workspace:FindFirstChild('CastleNWRoofQ'..i) if q then q:Destroy() end end
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local TL=Color3.fromRGB(226,222,212)
local t={{'NW',-31.5,-27,12},{'NE',31.5,-27,12},{'SW',-31.5,27,12}}
for i=1,3 do
 local b,x,z,d=t[i]
 P('Castle'..b..'RoofRing1',Vector3.new(2.4,d+2,d+2),CFrame.new(x,24.2,z)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.ClayRoofTiles,TL,Enum.PartType.Cylinder)
 P('Castle'..b..'RoofRing2',Vector3.new(2.2,d-2.5,d-2.5),CFrame.new(x,26.5,z)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.ClayRoofTiles,TL,Enum.PartType.Cylinder)
 P('Castle'..b..'RoofRing3',Vector3.new(2,d-6,d-6),CFrame.new(x,28.6,z)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.ClayRoofTiles,TL,Enum.PartType.Cylinder)
 P('Castle'..b..'Finial',Vector3.new(3,3,3),CFrame.new(x,31.1,z),Enum.Material.CorrodedMetal,TL,Enum.PartType.Ball)
end
report('tower roofs A')
end)

run_chunk(8, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local TL=Color3.fromRGB(226,222,212)
local t={{'SE',31.5,27,12},{'GW',-11,-27,11},{'GE',11,-27,11}}
for i=1,3 do
 local b,x,z,d=t[i]
 P('Castle'..b..'RoofRing1',Vector3.new(2.4,d+2,d+2),CFrame.new(x,24.2,z)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.ClayRoofTiles,TL,Enum.PartType.Cylinder)
 P('Castle'..b..'RoofRing2',Vector3.new(2.2,d-2.5,d-2.5),CFrame.new(x,26.5,z)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.ClayRoofTiles,TL,Enum.PartType.Cylinder)
 P('Castle'..b..'RoofRing3',Vector3.new(2,d-6,d-6),CFrame.new(x,28.6,z)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.ClayRoofTiles,TL,Enum.PartType.Cylinder)
 P('Castle'..b..'Finial',Vector3.new(3,3,3),CFrame.new(x,31.1,z),Enum.Material.CorrodedMetal,TL,Enum.PartType.Ball)
 P('Castle'..b..'Spire',Vector3.new(2.4,0.4,0.4),CFrame.new(x,33.8,z)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Metal,TL,Enum.PartType.Cylinder)
end
report('tower roofs B')
end)

run_chunk(9, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local TL=Color3.fromRGB(230,222,208)
local ang={45,135,225,315}
local ox={1,1,-1,-1}
local oz={-1,1,1,-1}
local T={{'NW',-31.5,-27,21.6,13},{'NE',31.5,-27,21.6,13},{'SW',-31.5,27,21.6,13}}
for i=1,3 do
 local b=T[i][1] local x=T[i][2] local z=T[i][3] local yb=T[i][4] local w=T[i][5]
 for q=1,4 do
  P('CastleTower'..b..'ConeQ'..q,Vector3.new(w,6,w),CFrame.new(x+ox[q]*w/2,yb+3,z+oz[q]*w/2)*CFrame.Angles(0,math.rad(ang[q]),0),Enum.Material.ClayRoofTiles,TL,Enum.PartType.CornerWedge)
 end
 P('CastleTower'..b..'FinialBall',Vector3.new(2.4,2.4,2.4),CFrame.new(x,yb+7.2,z),Enum.Material.CorrodedMetal,Color3.fromRGB(224,220,210),Enum.PartType.Ball)
end
report('cone roofs A')
end)

run_chunk(10, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local TL=Color3.fromRGB(230,222,208)
local ang={45,135,225,315}
local ox={1,1,-1,-1}
local oz={-1,1,1,-1}
local T={{'SE',31.5,27,21.6,13},{'GW',-11,-27,23.6,12},{'GE',11,-27,23.6,12}}
for i=1,3 do
 local b=T[i][1] local x=T[i][2] local z=T[i][3] local yb=T[i][4] local w=T[i][5]
 for q=1,4 do
  P('CastleTower'..b..'ConeQ'..q,Vector3.new(w,6,w),CFrame.new(x+ox[q]*w/2,yb+3,z+oz[q]*w/2)*CFrame.Angles(0,math.rad(ang[q]),0),Enum.Material.ClayRoofTiles,TL,Enum.PartType.CornerWedge)
 end
 P('CastleTower'..b..'FinialBall',Vector3.new(2.4,2.4,2.4),CFrame.new(x,yb+7.2,z),Enum.Material.CorrodedMetal,Color3.fromRGB(224,220,210),Enum.PartType.Ball)
end
report('cone roofs B')
end)

run_chunk(11, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(226,222,212)
local DK=Color3.fromRGB(206,200,190)
local WO=Color3.fromRGB(178,132,84)
-- gate arch voussoirs
for i=1,7 do
 local a=(i-1)*22
 P('CastleGateArch'..i,Vector3.new(1.4,3.4,3.4),CFrame.new(-4.2+i*1.4,12.4,0)*CFrame.new(0,0,0)*CFrame.Angles(0,0,math.rad(-90+a)),Enum.Material.Limestone,DK)
end
P('CastleGateArchCrown',Vector3.new(2,2.4,3.6),CFrame.new(0,13.6,-27),Enum.Material.Limestone,DK)
-- portcullis bars (thin but vertical, do not close the opening)
for i=1,7 do
 P('CastlePortcullisBar'..i,Vector3.new(0.45,9,0.45),CFrame.new(-3.9+i*1.3,4.6,-27.6),Enum.Material.CorrodedMetal,Color3.fromRGB(214,196,168),Enum.PartType.Cylinder)
end
P('CastlePortcullisRail1',Vector3.new(9,0.5,0.5),CFrame.new(0,8.9,-27.6),Enum.Material.CorrodedMetal,Color3.fromRGB(214,196,168),Enum.PartType.Cylinder)
P('CastlePortcullisRail2',Vector3.new(9,0.5,0.5),CFrame.new(0,3.2,-27.6),Enum.Material.CorrodedMetal,Color3.fromRGB(214,196,168),Enum.PartType.Cylinder)
-- drawbridge over the north moat
P('CastleDrawbridge',Vector3.new(9,0.8,15),CFrame.new(0,1.9,-34.5)*CFrame.Angles(math.rad(-6),0,0),Enum.Material.Wood,WO)
P('CastleDrawbridgeRailW',Vector3.new(0.5,0.5,15),CFrame.new(-4.2,2.9,-34.5)*CFrame.Angles(math.rad(-6),0,0),Enum.Material.Iron,Color3.fromRGB(190,180,170),Enum.PartType.Cylinder)
P('CastleDrawbridgeRailE',Vector3.new(0.5,0.5,15),CFrame.new(4.2,2.9,-34.5)*CFrame.Angles(math.rad(-6),0,0),Enum.Material.Iron,Color3.fromRGB(190,180,170),Enum.PartType.Cylinder)
P('CastleDrawbridgeChord',Vector3.new(9,0.5,0.5),CFrame.new(0,1.7,-42),Enum.Material.Iron,Color3.fromRGB(190,180,170),Enum.PartType.Cylinder)
for i=1,4 do
 P('CastleDrawbridgeChain'..i,Vector3.new(0.4,5,0.4),CFrame.new(-3.3+i*2.2,13,-26)*CFrame.Angles(math.rad(90),0,0),Enum.Material.CorrodedMetal,Color3.fromRGB(200,190,178),Enum.PartType.Cylinder)
end
report('gate arch, portcullis, drawbridge')
end)

run_chunk(12, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local DK=Color3.fromRGB(204,198,188)
local WO=Color3.fromRGB(180,134,86)
-- great hall, west side of courtyard
P('CastleHallBody',Vector3.new(16,9,26),CFrame.new(-22,4.5,4),Enum.Material.Plaster,ST)
P('CastleHallTimberN',Vector3.new(16.6,1,0.5),CFrame.new(-22,7.6,-9.2),Enum.Material.Wood,WO)
P('CastleHallTimberS',Vector3.new(16.6,1,0.5),CFrame.new(-22,7.6,17.2),Enum.Material.Wood,WO)
P('CastleHallTimberE1',Vector3.new(0.5,1,26.6),CFrame.new(-14.2,7.6,4),Enum.Material.Wood,WO)
P('CastleHallTimberE2',Vector3.new(0.5,1,26.6),CFrame.new(-14.2,4.2,4),Enum.Material.Wood,WO)
P('CastleHallBase',Vector3.new(16.6,1.4,26.6),CFrame.new(-22,0.7,4),Enum.Material.Granite,DK)
P('CastleHallRoofW',Vector3.new(9.5,1.2,28),CFrame.new(-26,12.1,4)*CFrame.Angles(0,0,math.rad(32)),Enum.Material.RoofShingles,Color3.fromRGB(210,206,204))
P('CastleHallRoofE',Vector3.new(9.5,1.2,28),CFrame.new(-18,12.1,4)*CFrame.Angles(0,0,math.rad(-32)),Enum.Material.RoofShingles,Color3.fromRGB(210,206,204))
P('CastleHallRidge',Vector3.new(1.2,1.2,28.4),CFrame.new(-22,15.9,4),Enum.Material.Wood,WO)
P('CastleHallDoor',Vector3.new(0.5,4.4,3),CFrame.new(-14,2.2,-2),Enum.Material.Wood,Color3.fromRGB(146,100,62))
P('CastleHallWindow1',Vector3.new(0.4,2.4,1.4),CFrame.new(-14.3,5.6,4),Enum.Material.Glass,Color3.fromRGB(128,176,196))
P('CastleHallWindow2',Vector3.new(0.4,2.4,1.4),CFrame.new(-14.3,5.6,10),Enum.Material.Glass,Color3.fromRGB(128,176,196))
P('CastleHallChimney',Vector3.new(2.6,9,2.6),CFrame.new(-24.5,19,12),Enum.Material.Brick,Color3.fromRGB(216,206,196))
P('CastleHallChimneyCap',Vector3.new(3.2,0.8,3.2),CFrame.new(-24.5,24,12),Enum.Material.Slate,Color3.fromRGB(210,206,206))
P('CastleHallLantern',Vector3.new(1.6,2,1.6),CFrame.new(-13.4,4.6,-2),Enum.Material.Neon,Color3.fromRGB(255,186,96))
P('CastleHallLanternCap',Vector3.new(2.2,0.5,2.2),CFrame.new(-13.4,5.85,-2),Enum.Material.CorrodedMetal,DK)
report('great hall')
end)

run_chunk(13, function()
for i=1,7 do local q=workspace:FindFirstChild('CastleGateArch'..i) if q then q:Destroy() end end
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(208,202,192)
for i=1,7 do
 local a=math.rad(10+(i-1)*25)
 local x=-4.6*math.cos(a)
 local y=9.2+4.6*math.sin(a)
 P('CastleGateArchVoussoir'..i,Vector3.new(1.5,2.2,3.6),CFrame.new(x,y,-27)*CFrame.Angles(0,0,a-math.rad(90)),Enum.Material.Limestone,DK)
end
P('CastleGateKeystone',Vector3.new(2,2.6,4),CFrame.new(0,14.2,-27),Enum.Material.Granite,Color3.fromRGB(214,208,198))
P('CastleDrawbridgeRailW',Vector3.new(0.5,0.5,15),CFrame.new(-4.2,2.9,-34.5)*CFrame.Angles(math.rad(-6),0,0),Enum.Material.CorrodedMetal,Color3.fromRGB(200,190,178),Enum.PartType.Cylinder)
P('CastleDrawbridgeRailE',Vector3.new(0.5,0.5,15),CFrame.new(4.2,2.9,-34.5)*CFrame.Angles(math.rad(-6),0,0),Enum.Material.CorrodedMetal,Color3.fromRGB(200,190,178),Enum.PartType.Cylinder)
P('CastleDrawbridgeChord',Vector3.new(9,0.5,0.5),CFrame.new(0,1.9,-42)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.CorrodedMetal,Color3.fromRGB(200,190,178),Enum.PartType.Cylinder)
for i=1,4 do
 P('CastleGateChain'..i,Vector3.new(0.45,5,0.45),CFrame.new(-3.3+i*2.2,13.2,-26)*CFrame.Angles(math.rad(90),0,math.rad(0)),Enum.Material.CorrodedMetal,Color3.fromRGB(198,188,176),Enum.PartType.Cylinder)
end
report('gate arch rebuilt')
end)

run_chunk(14, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(226,222,212)
local WO=Color3.fromRGB(184,138,88)
P('CastleWellCurb',Vector3.new(6,3,6),CFrame.new(22,1.5,-12)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Cobblestone,ST,Enum.PartType.Cylinder)
P('CastleWellRim',Vector3.new(6.8,0.7,6.8),CFrame.new(22,3.3,-12)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Limestone,Color3.fromRGB(212,206,196),Enum.PartType.Cylinder)
P('CastleWellPostW',Vector3.new(0.7,7,0.7),CFrame.new(20.2,5.4,-12),Enum.Material.Wood,WO)
P('CastleWellPostE',Vector3.new(0.7,7,0.7),CFrame.new(23.8,5.4,-12),Enum.Material.Wood,WO)
P('CastleWellAxle',Vector3.new(0.5,0.5,4.6),CFrame.new(22,8.2,-12)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,Color3.fromRGB(170,126,80),Enum.PartType.Cylinder)
P('CastleWellBucket',Vector3.new(1.7,1.7,1.7),CFrame.new(22,4.3,-12),Enum.Material.Wood,Color3.fromRGB(176,130,82),Enum.PartType.Cylinder)
P('CastleWellRope',Vector3.new(0.25,3.6,0.25),CFrame.new(22,6.3,-12),Enum.Material.Fabric,Color3.fromRGB(198,180,144))
P('CastleWellRoofW',Vector3.new(5,0.6,5.6),CFrame.new(21.2,9.6,-12)*CFrame.Angles(0,0,math.rad(26)),Enum.Material.RoofShingles,Color3.fromRGB(216,212,208))
P('CastleWellRoofE',Vector3.new(5,0.6,5.6),CFrame.new(22.8,9.6,-12)*CFrame.Angles(0,0,math.rad(-26)),Enum.Material.RoofShingles,Color3.fromRGB(216,212,208))
P('CastleWellRoofRidge',Vector3.new(1,1,5.8),CFrame.new(22,11.1,-12),Enum.Material.Wood,WO)
P('CastleWellWinding',Vector3.new(1.6,0.8,1.6),CFrame.new(24.6,7.4,-12),Enum.Material.CorrodedMetal,Color3.fromRGB(196,186,172),Enum.PartType.Cylinder)
report('well')
end)

run_chunk(15, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(226,222,212)
local WO=Color3.fromRGB(182,136,86)
local DK=Color3.fromRGB(206,200,190)
-- market stall NE
P('CastleStallTableA',Vector3.new(7,0.5,4),CFrame.new(16,2.2,-20),Enum.Material.WoodPlanks,Color3.fromRGB(196,152,104))
for i=1,4 do
 P('CastleStallLegA'..i,Vector3.new(0.5,2,0.5),CFrame.new(13.2+(i%2)*5.6,1,-21.6+math.floor((i-1)/2)*3.2),Enum.Material.Wood,WO)
end
P('CastleStallPostA1',Vector3.new(0.45,5,0.45),CFrame.new(13.2,2.5,-21.6),Enum.Material.Wood,WO)
P('CastleStallPostA2',Vector3.new(0.45,5,0.45),CFrame.new(18.8,2.5,-21.6),Enum.Material.Wood,WO)
P('CastleStallPostA3',Vector3.new(0.45,5,0.45),CFrame.new(13.2,2.5,-18.4),Enum.Material.Wood,WO)
P('CastleStallPostA4',Vector3.new(0.45,5,0.45),CFrame.new(18.8,2.5,-18.4),Enum.Material.Wood,WO)
P('CastleStallAwningW',Vector3.new(4,0.4,4.4),CFrame.new(14.6,5.4,-20)*CFrame.Angles(0,0,math.rad(22)),Enum.Material.Fabric,Color3.fromRGB(186,64,58))
P('CastleStallAwningE',Vector3.new(4,0.4,4.4),CFrame.new(17.4,5.4,-20)*CFrame.Angles(0,0,math.rad(-22)),Enum.Material.Fabric,Color3.fromRGB(186,64,58))
P('CastleStallGoods1',Vector3.new(1.4,1.4,1.4),CFrame.new(14.5,3.1,-20.6),Enum.Material.CeramicTiles,Color3.fromRGB(212,150,110),Enum.PartType.Ball)
P('CastleStallGoods2',Vector3.new(1.2,1.2,1.2),CFrame.new(16.4,3,-20.2),Enum.Material.CeramicTiles,Color3.fromRGB(206,168,120),Enum.PartType.Ball)
P('CastleStallCrateA',Vector3.new(2.6,2.6,2.6),CFrame.new(21,1.3,-21),Enum.Material.Wood,Color3.fromRGB(172,128,80))
P('CastleStallCrateB',Vector3.new(2.4,2.4,2.4),CFrame.new(20.4,3.8,-20.6)*CFrame.Angles(0,math.rad(18),0),Enum.Material.Wood,Color3.fromRGB(178,134,86))
P('CastleStallBarrelA',Vector3.new(3.2,3.2,3.2),CFrame.new(24.2,1.6,-18.6),Enum.Material.Wood,Color3.fromRGB(170,126,78),Enum.PartType.Cylinder)
P('CastleStallBarrelB',Vector3.new(2.8,2.8,2.8),CFrame.new(24.6,4.3,-18.6),Enum.Material.Wood,Color3.fromRGB(176,132,84),Enum.PartType.Cylinder)
report('market stall')
end)

run_chunk(16, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local WO=Color3.fromRGB(182,136,86)
local DK=Color3.fromRGB(206,200,190)
-- second stall, SW courtyard
P('CastleStallBTable',Vector3.new(6,0.5,4.4),CFrame.new(-16,2.2,-18)*CFrame.Angles(0,math.rad(-14),0),Enum.Material.WoodPlanks,Color3.fromRGB(196,152,104))
P('CastleStallBPost1',Vector3.new(0.45,5,0.45),CFrame.new(-18.9,2.5,-19.9),Enum.Material.Wood,WO)
P('CastleStallBPost2',Vector3.new(0.45,5,0.45),CFrame.new(-13.1,2.5,-19.9),Enum.Material.Wood,WO)
P('CastleStallBPost3',Vector3.new(0.45,5,0.45),CFrame.new(-18.9,2.5,-16.1),Enum.Material.Wood,WO)
P('CastleStallBPost4',Vector3.new(0.45,5,0.45),CFrame.new(-13.1,2.5,-16.1),Enum.Material.Wood,WO)
P('CastleStallBAwning',Vector3.new(7,0.4,4.8),CFrame.new(-16,5.3,-18)*CFrame.Angles(0,math.rad(-6),math.rad(7)),Enum.Material.Fabric,Color3.fromRGB(72,104,164))
P('CastleStallBPot1',Vector3.new(1.5,1.5,1.5),CFrame.new(-17.4,3.1,-18.4),Enum.Material.CeramicTiles,Color3.fromRGB(214,156,116),Enum.PartType.Ball)
P('CastleStallBPot2',Vector3.new(1.3,1.3,1.3),CFrame.new(-15.2,3,-17.8),Enum.Material.CeramicTiles,Color3.fromRGB(206,172,126),Enum.PartType.Ball)
P('CastleBenchW1',Vector3.new(4.5,0.5,1.4),CFrame.new(-8,1.6,-14),Enum.Material.WoodPlanks,Color3.fromRGB(190,146,98))
P('CastleBenchW2',Vector3.new(0.6,1.6,1.3),CFrame.new(-9.8,0.8,-14),Enum.Material.Wood,WO)
P('CastleBenchW3',Vector3.new(0.6,1.6,1.3),CFrame.new(-6.2,0.8,-14),Enum.Material.Wood,WO)
P('CastleBenchW4',Vector3.new(4.5,1.4,0.4),CFrame.new(-8,2.4,-14.6),Enum.Material.Wood,WO)
-- campfire
P('CastleFirePitRing',Vector3.new(5,1.4,5),CFrame.new(10,0.7,-3)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Basalt,DK,Enum.PartType.Cylinder)
P('CastleFireLog1',Vector3.new(0.9,0.9,4.2),CFrame.new(10,1.2,-3)*CFrame.Angles(0,math.rad(40),0),Enum.Material.Wood,Color3.fromRGB(120,92,64),Enum.PartType.Cylinder)
P('CastleFireLog2',Vector3.new(0.9,0.9,4.2),CFrame.new(10,1.4,-3)*CFrame.Angles(0,math.rad(-40),0),Enum.Material.Wood,Color3.fromRGB(120,92,64),Enum.PartType.Cylinder)
P('CastleFireEmber',Vector3.new(2.2,2.2,2.2),CFrame.new(10,1.8,-3),Enum.Material.CrackedLava,Color3.fromRGB(255,140,50),Enum.PartType.Ball)
for i=1,4 do
 local a=math.rad(45+i*90)
 P('CastleFireStone'..i,Vector3.new(1.4,1.4,1.4),CFrame.new(10+math.cos(a)*3.2,0.7,-3+math.sin(a)*3.2),Enum.Material.Rock,DK,Enum.PartType.Ball)
end
-- training dummy + armour stand
P('CastleDummyPost',Vector3.new(0.8,6,0.8),CFrame.new(-4,3,14),Enum.Material.Wood,WO)
P('CastleDummyBody',Vector3.new(1.2,2.4,1.2),CFrame.new(-4,7.4,14),Enum.Material.WoodPlanks,Color3.fromRGB(190,146,98))
P('CastleDummyHead',Vector3.new(1.6,1.6,1.6),CFrame.new(-4,9.3,14),Enum.Material.Fabric,Color3.fromRGB(196,186,164),Enum.PartType.Ball)
P('CastleDummyArm',Vector3.new(5,0.6,0.6),CFrame.new(-4,7.8,14)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,WO,Enum.PartType.Cylinder)
report('courtyard life')
end)

run_chunk(17, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local WO=Color3.fromRGB(184,138,88)
local DK=Color3.fromRGB(208,202,192)
-- smithy / workshop SE courtyard
P('CastleSmithyBody',Vector3.new(14,8,12),CFrame.new(21,4,-2),Enum.Material.Brick,Color3.fromRGB(222,214,204))
P('CastleSmithyPlinth',Vector3.new(14.6,1.2,12.6),CFrame.new(21,0.6,-2),Enum.Material.Basalt,DK)
P('CastleSmithyRoofW',Vector3.new(8.4,1,13.6),CFrame.new(17.4,10.2,-2)*CFrame.Angles(0,0,math.rad(28)),Enum.Material.ClayRoofTiles,Color3.fromRGB(228,176,148))
P('CastleSmithyRoofE',Vector3.new(8.4,1,13.6),CFrame.new(24.6,10.2,-2)*CFrame.Angles(0,0,math.rad(-28)),Enum.Material.ClayRoofTiles,Color3.fromRGB(228,176,148))
P('CastleSmithyRidge',Vector3.new(1.1,1.1,13.8),CFrame.new(21,13.5,-2),Enum.Material.Wood,WO)
P('CastleSmithyDoor',Vector3.new(0.5,3.6,2.6),CFrame.new(14.1,1.8,-2),Enum.Material.Wood,Color3.fromRGB(150,104,64))
P('CastleSmithyForge',Vector3.new(4,2.6,4),CFrame.new(21,1.3,3),Enum.Material.Basalt,DK)
P('CastleSmithyAnvil',Vector3.new(1.4,1.8,2.4),CFrame.new(26,0.9,-5),Enum.Material.Metal,Color3.fromRGB(196,196,200))
P('CastleSmithyAnvilBase',Vector3.new(2,1.6,2),CFrame.new(26,0.8,-5),Enum.Material.Wood,Color3.fromRGB(120,92,64),Enum.PartType.Cylinder)
P('CastleSmithyStack',Vector3.new(2,7,2),CFrame.new(26,3.5,1.5),Enum.Material.Brick,Color3.fromRGB(222,214,204))
P('CastleSmithyStackCap',Vector3.new(2.6,0.7,2.6),CFrame.new(26,7.3,1.5),Enum.Material.Slate,Color3.fromRGB(210,208,210))
P('CastleSmithyBanner',Vector3.new(0.3,5,1.8),CFrame.new(14,5.4,-5.6),Enum.Material.Fabric,Color3.fromRGB(176,72,52))
P('CastlePavedCourt',Vector3.new(44,0.3,38),CFrame.new(0,0.15,-2),Enum.Material.Pavement,Color3.fromRGB(224,220,212))
P('CastleCobblePathN',Vector3.new(7,0.4,20),CFrame.new(0,0.3,-18),Enum.Material.Pebble,Color3.fromRGB(222,218,208))
P('CastleCobblePathS',Vector3.new(6,0.4,18),CFrame.new(0,0.3,16),Enum.Material.Pebble,Color3.fromRGB(222,218,208))
report('smithy + paving')
end)

run_chunk(18, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local DK=Color3.fromRGB(208,202,192)
local WO=Color3.fromRGB(184,138,88)
-- road from drawbridge to the north
P('CastleRoadNorth',Vector3.new(9,0.5,22),CFrame.new(0,0.25,-53),Enum.Material.Dirt,Color3.fromRGB(216,206,190))
P('CastleRoadNorthEdge1',Vector3.new(1.2,0.5,20),CFrame.new(-4.8,0.25,-53),Enum.Material.Dirt,Color3.fromRGB(210,200,184))
P('CastleRoadNorthEdge2',Vector3.new(1.2,0.5,20),CFrame.new(4.8,0.25,-53),Enum.Material.Dirt,Color3.fromRGB(210,200,184))
-- road east
P('CastleRoadEast',Vector3.new(24,0.5,7),CFrame.new(56,0.25,6),Enum.Material.Pebble,Color3.fromRGB(222,216,204))
-- outer farmstead NE
P('CastleFarmHouseBody',Vector3.new(13,6.5,11),CFrame.new(20,3.25,52)*CFrame.Angles(0,math.rad(-8),0),Enum.Material.Plaster,ST)
P('CastleFarmHouseBase',Vector3.new(13.6,1.2,11.6),CFrame.new(20,0.6,52),Enum.Material.Cobblestone,DK)
P('CastleFarmHouseRoofW',Vector3.new(7.6,0.9,12.4),CFrame.new(16.4,7.9,52)*CFrame.Angles(0,math.rad(-8),math.rad(30)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleFarmHouseRoofE',Vector3.new(7.6,0.9,12.4),CFrame.new(23.6,7.9,52)*CFrame.Angles(0,math.rad(-8),math.rad(-30)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleFarmHouseDoor',Vector3.new(1.2,3.4,0.4),CFrame.new(20,1.7,46.5)*CFrame.Angles(0,math.rad(-8),0),Enum.Material.Wood,Color3.fromRGB(150,104,64))
P('CastleFarmShed',Vector3.new(8,4.4,7),CFrame.new(33,2.2,49)*CFrame.Angles(0,math.rad(14),0),Enum.Material.WoodPlanks,Color3.fromRGB(196,152,104))
P('CastleFarmShedRoof',Vector3.new(8.8,0.8,7.8),CFrame.new(33,5.1,49)*CFrame.Angles(0,math.rad(14),math.rad(12)),Enum.Material.RoofShingles,Color3.fromRGB(212,206,204))
-- watchtower beyond the SW corner
P('CastleWatchTowerPost1',Vector3.new(0.9,11,0.9),CFrame.new(-44,5.5,-44),Enum.Material.Wood,WO)
P('CastleWatchTowerPost2',Vector3.new(0.9,11,0.9),CFrame.new(-38,5.5,-44),Enum.Material.Wood,WO)
P('CastleWatchTowerPost3',Vector3.new(0.9,11,0.9),CFrame.new(-44,5.5,-38),Enum.Material.Wood,WO)
P('CastleWatchTowerPost4',Vector3.new(0.9,11,0.9),CFrame.new(-38,5.5,-38),Enum.Material.Wood,WO)
P('CastleWatchTowerDeck',Vector3.new(8,0.7,8),CFrame.new(-41,11.2,-41),Enum.Material.WoodPlanks,Color3.fromRGB(190,146,98))
P('CastleWatchTowerRail1',Vector3.new(8,1.5,0.6),CFrame.new(-41,12.2,-45),Enum.Material.Wood,WO)
P('CastleWatchTowerRail2',Vector3.new(8,1.5,0.6),CFrame.new(-41,12.2,-37),Enum.Material.Wood,WO)
P('CastleWatchTowerRail3',Vector3.new(0.6,1.5,8),CFrame.new(-45,12.2,-41),Enum.Material.Wood,WO)
P('CastleWatchTowerRail4',Vector3.new(0.6,1.5,8),CFrame.new(-37,12.2,-41),Enum.Material.Wood,WO)
P('CastleWatchTowerRoof',Vector3.new(9.6,0.9,9.6),CFrame.new(-41,14.6,-41)*CFrame.Angles(0,math.rad(45),0),Enum.Material.RoofShingles,Color3.fromRGB(206,200,202))
P('CastleWatchTowerRoofB',Vector3.new(9.6,0.9,9.6),CFrame.new(-41,15.4,-41)*CFrame.Angles(0,math.rad(45),0),Enum.Material.RoofShingles,Color3.fromRGB(214,208,210))
P('CastleWatchTowerBeacon',Vector3.new(1.8,1.8,1.8),CFrame.new(-41,16.6,-41),Enum.Material.Neon,Color3.fromRGB(255,168,72),Enum.PartType.Ball)
report('outer farmstead + watchtower')
end)

run_chunk(19, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(212,206,196)
local LEAF=Color3.fromRGB(206,226,178)
-- tents NE of the road
P('CastleTentBody1',Vector3.new(9,6,9),CFrame.new(-34,3,-52)*CFrame.Angles(0,math.rad(20),0),Enum.Material.Fabric,Color3.fromRGB(212,196,168),Enum.PartType.Wedge)
P('CastleTentBody2',Vector3.new(9,6,9),CFrame.new(-44,3,-49)*CFrame.Angles(0,math.rad(-12),0),Enum.Material.Fabric,Color3.fromRGB(178,186,204),Enum.PartType.Wedge)
P('CastleTentPole1',Vector3.new(0.4,7,0.4),CFrame.new(-34,3.5,-52),Enum.Material.Wood,Color3.fromRGB(180,134,86))
P('CastleTentPole2',Vector3.new(0.4,7,0.4),CFrame.new(-44,3.5,-49),Enum.Material.Wood,Color3.fromRGB(180,134,86))
P('CastleTentPole3',Vector3.new(0.4,7,0.4),CFrame.new(-39,3.5,-55),Enum.Material.Wood,Color3.fromRGB(180,134,86))
P('CastleCampfireRing',Vector3.new(4,1,4),CFrame.new(-39,0.5,-51)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Basalt,DK,Enum.PartType.Cylinder)
P('CastleCampEmber',Vector3.new(1.6,1.6,1.6),CFrame.new(-39,1.4,-51),Enum.Material.CrackedLava,Color3.fromRGB(255,132,46),Enum.PartType.Ball)
-- trees
local T={{-50,10,1.15,0},{-52,24,1,0.4},{-48,-8,1.25,0},{-30,50,1.05,0.2},{26,58,1.1,0.35},{44,44,0.95,0},{-8,56,1.2,0.1},{50,-20,1.05,0.3},{-20,-58,1.1,0.45},{10,-60,0.9,0.15}}
for i=1,10 do
 local x,z,s,r=T[i][1],T[i][2],T[i][3],T[i][4]
 P('CastleTreeTrunk'..i,Vector3.new(1.4*s,7*s,1.4*s),CFrame.new(x,3.5*s,z),Enum.Material.Wood,Color3.fromRGB(190,144,88),Enum.PartType.Cylinder)
 P('CastleTreeCanopy'..i,Vector3.new(7*s,6*s,7*s),CFrame.new(x,8.4*s,z),Enum.Material.LeafyGrass,LEAF,Enum.PartType.Ball)
 P('CastleTreeCanopyTop'..i,Vector3.new(4.6*s,4.2*s,4.6*s),CFrame.new(x+math.sin(r)*1.2,12.2*s,z+math.cos(r)*1.2),Enum.Material.LeafyGrass,Color3.fromRGB(212,232,184),Enum.PartType.Ball)
end
report('tents + trees')
end)

run_chunk(20, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local DK=Color3.fromRGB(208,202,192)
local WO=Color3.fromRGB(184,138,88)
P('CastleRoadNorth',Vector3.new(9,0.5,22),CFrame.new(0,0.25,-53),Enum.Material.Ground,Color3.fromRGB(214,198,166))
P('CastleRoadNorthEdge1',Vector3.new(1.2,0.5,20),CFrame.new(-4.8,0.25,-53),Enum.Material.Mud,Color3.fromRGB(206,188,158))
P('CastleRoadNorthEdge2',Vector3.new(1.2,0.5,20),CFrame.new(4.8,0.25,-53),Enum.Material.Mud,Color3.fromRGB(206,188,158))
P('CastleRoadEast',Vector3.new(24,0.5,7),CFrame.new(56,0.25,6),Enum.Material.Pebble,Color3.fromRGB(222,216,204))
P('CastleFarmHouseBody',Vector3.new(13,6.5,11),CFrame.new(20,3.25,52)*CFrame.Angles(0,math.rad(-8),0),Enum.Material.Plaster,ST)
P('CastleFarmHouseBase',Vector3.new(13.6,1.2,11.6),CFrame.new(20,0.6,52),Enum.Material.Cobblestone,DK)
P('CastleFarmHouseRoofW',Vector3.new(7.6,0.9,12.4),CFrame.new(16.4,7.9,52)*CFrame.Angles(0,math.rad(-8),math.rad(30)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleFarmHouseRoofE',Vector3.new(7.6,0.9,12.4),CFrame.new(23.6,7.9,52)*CFrame.Angles(0,math.rad(-8),math.rad(-30)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleFarmHouseDoor',Vector3.new(1.2,3.4,0.4),CFrame.new(20,1.7,46.5)*CFrame.Angles(0,math.rad(-8),0),Enum.Material.Wood,Color3.fromRGB(150,104,64))
P('CastleFarmShed',Vector3.new(8,4.4,7),CFrame.new(33,2.2,49)*CFrame.Angles(0,math.rad(14),0),Enum.Material.WoodPlanks,Color3.fromRGB(196,152,104))
P('CastleFarmShedRoof',Vector3.new(8.8,0.8,7.8),CFrame.new(33,5.1,49)*CFrame.Angles(0,math.rad(14),math.rad(12)),Enum.Material.RoofShingles,Color3.fromRGB(212,206,204))
P('CastleWatchTowerPost1',Vector3.new(0.9,11,0.9),CFrame.new(-44,5.5,-44),Enum.Material.Wood,WO)
P('CastleWatchTowerPost2',Vector3.new(0.9,11,0.9),CFrame.new(-38,5.5,-44),Enum.Material.Wood,WO)
P('CastleWatchTowerPost3',Vector3.new(0.9,11,0.9),CFrame.new(-44,5.5,-38),Enum.Material.Wood,WO)
P('CastleWatchTowerPost4',Vector3.new(0.9,11,0.9),CFrame.new(-38,5.5,-38),Enum.Material.Wood,WO)
P('CastleWatchTowerDeck',Vector3.new(8,0.7,8),CFrame.new(-41,11.2,-41),Enum.Material.WoodPlanks,Color3.fromRGB(190,146,98))
P('CastleWatchTowerRail1',Vector3.new(8,1.5,0.6),CFrame.new(-41,12.2,-45),Enum.Material.Wood,WO)
P('CastleWatchTowerRail2',Vector3.new(8,1.5,0.6),CFrame.new(-41,12.2,-37),Enum.Material.Wood,WO)
P('CastleWatchTowerRail3',Vector3.new(0.6,1.5,8),CFrame.new(-45,12.2,-41),Enum.Material.Wood,WO)
P('CastleWatchTowerRail4',Vector3.new(0.6,1.5,8),CFrame.new(-37,12.2,-41),Enum.Material.Wood,WO)
P('CastleWatchTowerRoof',Vector3.new(9.6,0.9,9.6),CFrame.new(-41,14.6,-41)*CFrame.Angles(0,math.rad(45),0),Enum.Material.RoofShingles,Color3.fromRGB(206,200,202))
P('CastleWatchTowerBeacon',Vector3.new(1.8,1.8,1.8),CFrame.new(-41,16.6,-41),Enum.Material.Neon,Color3.fromRGB(255,168,72),Enum.PartType.Ball)
report('farmstead + watchtower rebuilt')
end)

run_chunk(21, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local WO=Color3.fromRGB(184,138,88)
local DK=Color3.fromRGB(212,206,196)
-- wall-walk stairs inside west wall
for i=1,6 do
 P('CastleWallStair'..i,Vector3.new(6,1,2.2),CFrame.new(-28,0.5+i*0.95,20-i*2.1),Enum.Material.Limestone,DK)
end
P('CastleWallStairLanding',Vector3.new(6,1,6),CFrame.new(-28,6.7,13.5),Enum.Material.Limestone,DK)
-- merlons along curtain wall crests
for i=1,13 do
 P('CastleMerlonNorth'..i,Vector3.new(1.8,2,3.2),CFrame.new(-30+i*5,14.5,-27),Enum.Material.Cobblestone,Color3.fromRGB(222,218,208))
end
for i=1,10 do
 P('CastleMerlonSouth'..i,Vector3.new(1.8,2,3.2),CFrame.new(-27+i*6,14.5,27),Enum.Material.Cobblestone,Color3.fromRGB(222,218,208))
end
-- torch brackets on inner wall faces
for i=1,5 do
 P('CastleTorchBracket'..i,Vector3.new(0.6,0.6,2.2),CFrame.new(-30,7,-25.6+i*11),Enum.Material.CorrodedMetal,DK,Enum.PartType.Cylinder)
 P('CastleTorchFlame'..i,Vector3.new(1.2,1.6,1.2),CFrame.new(-30,7.4,-24.5+i*11),Enum.Material.Neon,Color3.fromRGB(255,178,84),Enum.PartType.Ball)
end
report('wall walk, merlons, torches')
end)

run_chunk(22, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(212,206,196)
local WO=Color3.fromRGB(182,136,86)
-- garden plots inside the walls, SE
P('CastleGardenSoil1',Vector3.new(10,0.4,5),CFrame.new(22,0.4,16),Enum.Material.Ground,Color3.fromRGB(206,186,152))
P('CastleGardenSoil2',Vector3.new(5,0.4,9),CFrame.new(27,0.4,8),Enum.Material.Ground,Color3.fromRGB(202,182,148))
P('CastleGardenFence1',Vector3.new(10.4,1.4,0.4),CFrame.new(22,0.7,18.7),Enum.Material.Wood,WO)
P('CastleGardenFence2',Vector3.new(0.4,1.4,9.4),CFrame.new(24.7,0.7,8),Enum.Material.Wood,WO)
for i=1,4 do
 P('CastleGardenRow'..i,Vector3.new(9,0.8,0.8),CFrame.new(22,1,14+i*1.2),Enum.Material.LeafyGrass,Color3.fromRGB(210,232,180))
end
-- rocks and boulders scattered outside
local R={{-58,-30,4.4},{-56,36,3.2},{58,-14,5},{-40,54,3.6},{44,-56,4.2},{12,60,3.2},{-62,6,3.8},{62,44,3.4},{-24,-46,2.6},{30,-50,3}}
for i=1,10 do
 local x,z,s=R[i][1],R[i][2],R[i][3]
 P('CastleBoulder'..i,Vector3.new(s,s*0.8,s),CFrame.new(x,s*0.4,z)*CFrame.Angles(0,math.rad(i*17),0),Enum.Material.Rock,DK)
end
-- fence line along the road
for i=1,8 do
 P('CastleRoadFencePost'..i,Vector3.new(0.5,2.4,0.5),CFrame.new(6.5,1.2,-44-i*4),Enum.Material.Wood,WO)
 P('CastleRoadFenceRail'..i,Vector3.new(0.3,0.3,4.2),CFrame.new(6.5,1.9,-46-i*4),Enum.Material.Wood,Color3.fromRGB(176,130,82),Enum.PartType.Cylinder)
end
report('garden, boulders, road fence')
end)

run_chunk(23, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(212,206,196)
local WO=Color3.fromRGB(184,138,88)
local ST=Color3.fromRGB(228,224,212)
-- moat water north (outside the wall, dark trench strip)
P('CastleMoatNorth',Vector3.new(70,0.3,7),CFrame.new(0,0.15,-31.5),Enum.Material.Ice,Color3.fromRGB(150,178,196))
P('CastleMoatBankN',Vector3.new(72,1.2,3),CFrame.new(0,0.6,-35.5),Enum.Material.Ground,Color3.fromRGB(198,182,148))
P('CastleMoatBankS',Vector3.new(72,1.2,3),CFrame.new(0,0.6,-27.9),Enum.Material.Ground,Color3.fromRGB(198,182,148))
-- west flank outbuildings: chapel
P('CastleChapelBody',Vector3.new(11,9,15),CFrame.new(-22,4.5,-14),Enum.Material.Sandstone,ST)
P('CastleChapelBase',Vector3.new(11.6,1.2,15.6),CFrame.new(-22,0.6,-14),Enum.Material.Granite,DK)
P('CastleChapelRoofW',Vector3.new(6.6,0.9,16.4),CFrame.new(-24.2,11.2,-14),Enum.Material.Slate,Color3.fromRGB(200,204,212))
P('CastleChapelRoofE',Vector3.new(6.6,0.9,16.4),CFrame.new(-19.8,11.2,-14),Enum.Material.Slate,Color3.fromRGB(200,204,212))
P('CastleChapelSteeple',Vector3.new(6,4,6),CFrame.new(-22,15.4,-21),Enum.Material.Sandstone,ST)
P('CastleChapelSteepleRoof',Vector3.new(6.4,4.6,6.4),CFrame.new(-22,19.7,-21)*CFrame.Angles(0,math.rad(45),0),Enum.Material.Slate,Color3.fromRGB(200,204,212),Enum.PartType.Wedge)
P('CastleChapelBelfry',Vector3.new(1.6,1.6,1.6),CFrame.new(-22,23.6,-21),Enum.Material.CorrodedMetal,DK,Enum.PartType.Ball)
P('CastleChapelRose',Vector3.new(0.4,3.4,3.4),CFrame.new(-16.6,7,-14),Enum.Material.Glass,Color3.fromRGB(126,172,198),Enum.PartType.Cylinder)
P('CastleChapelDoor',Vector3.new(0.5,4.6,2.4),CFrame.new(-16.6,2.3,-19),Enum.Material.Wood,Color3.fromRGB(148,102,62))
report('chapel + moat')
end)

run_chunk(24, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(210,204,194)
local WO=Color3.fromRGB(182,136,86)
local ST=Color3.fromRGB(228,224,212)
-- NE storage yard: barrels on a rack, cart
for i=1,4 do
 P('CastleRackBarrel'..i,Vector3.new(2.6,2.6,2.6),CFrame.new(18+i*2.9,1.3,-8),Enum.Material.Wood,Color3.fromRGB(174,130,82),Enum.PartType.Cylinder)
 P('CastleRackBarrel2'..i,Vector3.new(2.4,2.4,2.4),CFrame.new(18+i*2.9,3.6,-8),Enum.Material.Wood,Color3.fromRGB(178,134,86),Enum.PartType.Cylinder)
end
P('CastleRackPlank',Vector3.new(15,0.4,3),CFrame.new(23.8,2.4,-8),Enum.Material.WoodPlanks,Color3.fromRGB(196,152,104))
P('CastleCartBed',Vector3.new(9,0.6,5),CFrame.new(-8,1.8,20)*CFrame.Angles(0,math.rad(24),0),Enum.Material.WoodPlanks,Color3.fromRGB(190,146,98))
P('CastleCartSide1',Vector3.new(9,1.4,0.4),CFrame.new(-8,2.5,17.9)*CFrame.Angles(0,math.rad(24),0),Enum.Material.Wood,WO)
P('CastleCartSide2',Vector3.new(9,1.4,0.4),CFrame.new(-7.7,2.5,22.1)*CFrame.Angles(0,math.rad(24),0),Enum.Material.Wood,WO)
P('CastleCartWheelA',Vector3.new(4,4,0.4),CFrame.new(-11.6,2,22.2)*CFrame.Angles(0,math.rad(24),0),Enum.Material.Wood,Color3.fromRGB(140,104,66),Enum.PartType.Cylinder)
P('CastleCartWheelB',Vector3.new(4,4,0.4),CFrame.new(-4.4,2,17.8)*CFrame.Angles(0,math.rad(24),0),Enum.Material.Wood,Color3.fromRGB(140,104,66),Enum.PartType.Cylinder)
P('CastleCartShaft',Vector3.new(7,0.4,0.4),CFrame.new(-2.4,2.1,20.6)*CFrame.Angles(0,math.rad(24),math.rad(90)),Enum.Material.Wood,Color3.fromRGB(140,104,66),Enum.PartType.Cylinder)
P('CastleCartLoad1',Vector3.new(2.2,2.2,2.2),CFrame.new(-9.6,3.2,20.6),Enum.Material.Fabric,Color3.fromRGB(196,180,150),Enum.PartType.Ball)
P('CastleCartLoad2',Vector3.new(2,2,2),CFrame.new(-6.2,3.1,19.4),Enum.Material.CeramicTiles,Color3.fromRGB(208,160,120),Enum.PartType.Ball)
-- laundry line + laundry
P('CastleLinePost1',Vector3.new(0.4,5,0.4),CFrame.new(-26,2.5,20),Enum.Material.Wood,WO)
P('CastleLinePost2',Vector3.new(0.4,5,0.4),CFrame.new(-18,2.5,20),Enum.Material.Wood,WO)
P('CastleClothLine',Vector3.new(8,0.15,0.15),CFrame.new(-22,4.9,20),Enum.Material.Fabric,Color3.fromRGB(206,196,172),Enum.PartType.Cylinder)
for i=1,3 do
 P('CastleLaundry'..i,Vector3.new(1.6,2.4,0.2),CFrame.new(-24+i*2.4,3.7,20)*CFrame.Angles(0,0,math.rad(i*6-6)),Enum.Material.Fabric,Color3.fromRGB(214,206,190))
end
report('yard props')
end)

run_chunk(25, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(210,204,194)
local ST=Color3.fromRGB(228,224,212)
-- terraced ground: low grass platforms around the castle for depth
P('CastleTerrainNorth',Vector3.new(120,1,26),CFrame.new(0,0.5,-50),Enum.Material.Grass,Color3.fromRGB(206,226,178))
P('CastleTerrainSouth',Vector3.new(120,1,24),CFrame.new(0,0.5,54),Enum.Material.Grass,Color3.fromRGB(202,222,174))
P('CastleTerrainWest',Vector3.new(30,1,80),CFrame.new(-50,0.5,4),Enum.Material.Grass,Color3.fromRGB(200,220,172))
P('CastleTerrainEast',Vector3.new(26,1,80),CFrame.new(52,0.5,4),Enum.Material.LeafyGrass,Color3.fromRGB(204,224,176))
P('CastleTerrainNE',Vector3.new(46,2.2,26),CFrame.new(44,1.1,48),Enum.Material.Rock,Color3.fromRGB(198,198,186))
P('CastleTerrainSW',Vector3.new(34,1.8,22),CFrame.new(-46,0.9,-46),Enum.Material.Rock,Color3.fromRGB(196,196,184))
-- outer village: two cottages with tile roofs
P('CastleCottageBody1',Vector3.new(12,5.5,10),CFrame.new(46,4.2,-44)*CFrame.Angles(0,math.rad(12),0),Enum.Material.Plaster,ST)
P('CastleCottageRoof1a',Vector3.new(7.2,0.8,11.4),CFrame.new(42.8,8.4,-44)*CFrame.Angles(0,math.rad(12),math.rad(30)),Enum.Material.ClayRoofTiles,Color3.fromRGB(226,172,144))
P('CastleCottageRoof1b',Vector3.new(7.2,0.8,11.4),CFrame.new(49.2,8.4,-44)*CFrame.Angles(0,math.rad(12),math.rad(-30)),Enum.Material.ClayRoofTiles,Color3.fromRGB(226,172,144))
P('CastleCottageBody2',Vector3.new(10,5,9),CFrame.new(-50,3.9,42)*CFrame.Angles(0,math.rad(-20),0),Enum.Material.WoodPlanks,Color3.fromRGB(196,152,104))
P('CastleCottageRoof2a',Vector3.new(6.2,0.7,10.4),CFrame.new(-52.2,7.6,42)*CFrame.Angles(0,math.rad(-20),math.rad(28)),Enum.Material.RoofShingles,Color3.fromRGB(210,206,204))
P('CastleCottageRoof2b',Vector3.new(6.2,0.7,10.4),CFrame.new(-47.8,7.6,42)*CFrame.Angles(0,math.rad(-20),math.rad(-28)),Enum.Material.RoofShingles,Color3.fromRGB(210,206,204))
P('CastleCottageChimney',Vector3.new(1.8,5,1.8),CFrame.new(49.4,10.5,-41),Enum.Material.Brick,Color3.fromRGB(220,212,202))
P('CastleWellOuterCurb',Vector3.new(4.4,2.4,4.4),CFrame.new(-40,1.4,-16)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Cobblestone,ST,Enum.PartType.Cylinder)
P('CastleWellOuterRoofA',Vector3.new(4,0.5,5),CFrame.new(-41,4.2,-16)*CFrame.Angles(0,0,math.rad(26)),Enum.Material.Wood,Color3.fromRGB(172,128,80))
P('CastleWellOuterRoofB',Vector3.new(4,0.5,5),CFrame.new(-39,4.2,-16)*CFrame.Angles(0,0,math.rad(-26)),Enum.Material.Wood,Color3.fromRGB(172,128,80))
P('CastleSignpost',Vector3.new(0.5,5,0.5),CFrame.new(9,2.5,-40),Enum.Material.Wood,Color3.fromRGB(176,130,82))
P('CastleSignBoard',Vector3.new(4.4,1.4,0.3),CFrame.new(11,4.6,-40)*CFrame.Angles(0,math.rad(18),0),Enum.Material.WoodPlanks,Color3.fromRGB(200,158,108))
report('terrain + outer village')
end)

run_chunk(26, function()
for _,n in ipairs({'CastleMerlonNorth5','CastleMerlonNorth6','CastleMerlonNorth7'}) do local q=workspace:FindFirstChild(n) if q then q:Destroy() end end
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local DK=Color3.fromRGB(210,204,194)
P('CastleGround',Vector3.new(128,1,128),CFrame.new(0,-0.5,0),Enum.Material.Grass,Color3.fromRGB(206,226,180))
P('CastleCurtainNorthWest',Vector3.new(27.5,13.5,3),CFrame.new(-18.25,6.75,-27),Enum.Material.Cobblestone,ST)
P('CastleCurtainNorthEast',Vector3.new(27.5,13.5,3),CFrame.new(18.25,6.75,-27),Enum.Material.Cobblestone,ST)
P('CastleGateLintel',Vector3.new(10,2.5,3.2),CFrame.new(0,14.25,-27),Enum.Material.Granite,Color3.fromRGB(216,210,200))
P('CastleCurtainSouth',Vector3.new(64,13.5,3),CFrame.new(0,6.75,27),Enum.Material.Cobblestone,ST)
P('CastleCurtainEast',Vector3.new(3,13.5,51),CFrame.new(32,6.75,0),Enum.Material.Cobblestone,ST)
P('CastleCurtainWest',Vector3.new(3,13.5,51),CFrame.new(-32,6.75,0),Enum.Material.Cobblestone,ST)
P('CastleWallWalkNorthWest',Vector3.new(27.5,0.6,4),CFrame.new(-18.25,13.8,-27),Enum.Material.Pebble,Color3.fromRGB(222,218,208))
P('CastleWallWalkNorthEast',Vector3.new(27.5,0.6,4),CFrame.new(18.25,13.8,-27),Enum.Material.Pebble,Color3.fromRGB(222,218,208))
P('CastleWallWalkSouth',Vector3.new(64,0.6,4),CFrame.new(0,13.8,27),Enum.Material.Pebble,Color3.fromRGB(222,218,208))
P('CastleWallWalkEast',Vector3.new(4,0.6,51),CFrame.new(32,13.8,0),Enum.Material.Pebble,Color3.fromRGB(222,218,208))
P('CastleWallWalkWest',Vector3.new(4,0.6,51),CFrame.new(-32,13.8,0),Enum.Material.Pebble,Color3.fromRGB(222,218,208))
report('ground + curtain walls')
end)

run_chunk(27, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(208,202,192)
local ST=Color3.fromRGB(224,220,210)
-- buttresses
P('CastleButtressNorthWest',Vector3.new(4,16,3),CFrame.new(-18,8,-30.2),Enum.Material.Cobblestone,ST)
P('CastleButtressNorthEast',Vector3.new(4,16,3),CFrame.new(18,8,-30.2),Enum.Material.Cobblestone,ST)
P('CastleButtressSouthWest',Vector3.new(4,15,3),CFrame.new(-20,7.5,29.9),Enum.Material.Cobblestone,ST)
P('CastleButtressSouthEast',Vector3.new(4,15,3),CFrame.new(20,7.5,29.9),Enum.Material.Cobblestone,ST)
P('CastleButtressEastOne',Vector3.new(3,15,4),CFrame.new(33.9,7.5,-10),Enum.Material.Cobblestone,ST)
P('CastleButtressEastTwo',Vector3.new(3,15,4),CFrame.new(33.9,7.5,10),Enum.Material.Cobblestone,ST)
P('CastleButtressWestOne',Vector3.new(3,14,4),CFrame.new(-33.9,7,-10),Enum.Material.Cobblestone,ST)
P('CastleButtressWestTwo',Vector3.new(3,14,4),CFrame.new(-33.9,7,10),Enum.Material.Cobblestone,ST)
P('CastleButtressCapWest',Vector3.new(4.2,2.2,3.4),CFrame.new(-33.9,15.1,-10)*CFrame.Angles(0,0,math.rad(45)),Enum.Material.Slate,Color3.fromRGB(202,206,212),Enum.PartType.Wedge)
P('CastleButtressCapEast',Vector3.new(4.2,2.2,3.4),CFrame.new(33.9,15.1,10)*CFrame.Angles(0,0,math.rad(45)),Enum.Material.Slate,Color3.fromRGB(202,206,212),Enum.PartType.Wedge)
-- arrow slits
P('CastleArrowSlitN1',Vector3.new(0.5,2.6,0.4),CFrame.new(-24,8,-28.6),Enum.Material.Glass,Color3.fromRGB(96,120,132))
P('CastleArrowSlitN2',Vector3.new(0.5,2.6,0.4),CFrame.new(-12,8,-28.6),Enum.Material.Glass,Color3.fromRGB(96,120,132))
P('CastleArrowSlitN3',Vector3.new(0.5,2.6,0.4),CFrame.new(12,8,-28.6),Enum.Material.Glass,Color3.fromRGB(96,120,132))
P('CastleArrowSlitN4',Vector3.new(0.5,2.6,0.4),CFrame.new(24,8,-28.6),Enum.Material.Glass,Color3.fromRGB(96,120,132))
P('CastleArrowSlitS1',Vector3.new(0.5,2.6,0.4),CFrame.new(-14,8,28.6),Enum.Material.Glass,Color3.fromRGB(96,120,132))
P('CastleArrowSlitS2',Vector3.new(0.5,2.6,0.4),CFrame.new(14,8,28.6),Enum.Material.Glass,Color3.fromRGB(96,120,132))
P('CastleArrowSlitE1',Vector3.new(0.4,2.6,0.5),CFrame.new(33.6,8,-6),Enum.Material.Glass,Color3.fromRGB(96,120,132))
P('CastleArrowSlitE2',Vector3.new(0.4,2.6,0.5),CFrame.new(33.6,8,6),Enum.Material.Glass,Color3.fromRGB(96,120,132))
P('CastleArrowSlitW1',Vector3.new(0.4,2.6,0.5),CFrame.new(-33.6,8,-6),Enum.Material.Glass,Color3.fromRGB(96,120,132))
P('CastleArrowSlitW2',Vector3.new(0.4,2.6,0.5),CFrame.new(-33.6,8,6),Enum.Material.Glass,Color3.fromRGB(96,120,132))
P('CastleWallJoinNorthEast',Vector3.new(5,14.5,3.4),CFrame.new(29.5,7.25,-27),Enum.Material.Cobblestone,Color3.fromRGB(226,222,212))
P('CastleWallJoinSouthWest',Vector3.new(5,14.5,3.4),CFrame.new(-29.5,7.25,27),Enum.Material.Cobblestone,Color3.fromRGB(226,222,212))
report('buttresses and slits')
end)

run_chunk(28, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local DK=Color3.fromRGB(210,204,194)
local GL=Color3.fromRGB(120,164,188)
local RD=Color3.fromRGB(172,42,46)
local T={{'NW',-31.5,-27},{'NE',31.5,-27},{'SW',-31.5,27}}
for i=1,3 do
 local b=T[i][1] local x=T[i][2] local z=T[i][3]
 P('CastleTower'..b..'Body',Vector3.new(12,19,12),CFrame.new(x,9.5,z),Enum.Material.Cobblestone,ST)
 P('CastleTower'..b..'StringCourse',Vector3.new(12.8,1,12.8),CFrame.new(x,19.5,z),Enum.Material.Limestone,DK)
 P('CastleTower'..b..'CorbelDrum',Vector3.new(13,1.6,13),CFrame.new(x,20.8,z),Enum.Material.Cobblestone,ST)
 P('CastleTower'..b..'SlitEast',Vector3.new(0.5,2.8,0.7),CFrame.new(x+6.1,13,z),Enum.Material.Glass,GL)
 P('CastleTower'..b..'SlitNorth',Vector3.new(0.7,2.8,0.5),CFrame.new(x,13,z-6.1),Enum.Material.Glass,GL)
 P('CastleTower'..b..'Banner',Vector3.new(0.35,7,2.2),CFrame.new(x+6.2,10.5,z),Enum.Material.Fabric,RD)
end
report('corner towers A')
end)

run_chunk(29, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local DK=Color3.fromRGB(210,204,194)
local GL=Color3.fromRGB(120,164,188)
local RD=Color3.fromRGB(172,42,46)
P('CastleTowerSEBody',Vector3.new(12,19,12),CFrame.new(31.5,9.5,27),Enum.Material.Cobblestone,ST)
P('CastleTowerSEStringCourse',Vector3.new(12.8,1,12.8),CFrame.new(31.5,19.5,27),Enum.Material.Limestone,DK)
P('CastleTowerSECorbelDrum',Vector3.new(13,1.6,13),CFrame.new(31.5,20.8,27),Enum.Material.Cobblestone,ST)
P('CastleTowerSESlitWest',Vector3.new(0.5,2.8,0.7),CFrame.new(25.4,13,27),Enum.Material.Glass,GL)
P('CastleTowerSESlitSouth',Vector3.new(0.7,2.8,0.5),CFrame.new(31.5,13,33.1),Enum.Material.Glass,GL)
P('CastleTowerSEBanner',Vector3.new(2.2,7,0.35),CFrame.new(31.5,10.5,33.2),Enum.Material.Fabric,RD)
P('CastleGateTowerWestBody',Vector3.new(11,21.6,11),CFrame.new(-10,10.8,-27),Enum.Material.Cobblestone,ST)
P('CastleGateTowerWestCorbel',Vector3.new(12,2,12),CFrame.new(-10,22.6,-27),Enum.Material.Cobblestone,ST)
P('CastleGateTowerWestSlit',Vector3.new(0.5,2.8,0.7),CFrame.new(-10,13,-32.6),Enum.Material.Glass,GL)
P('CastleGateTowerWestBanner',Vector3.new(2.2,7,0.35),CFrame.new(-10,10.5,-32.2),Enum.Material.Fabric,RD)
P('CastleGateTowerEastBody',Vector3.new(11,21.6,11),CFrame.new(10,10.8,-27),Enum.Material.Cobblestone,ST)
P('CastleGateTowerEastCorbel',Vector3.new(12,2,12),CFrame.new(10,22.6,-27),Enum.Material.Cobblestone,ST)
P('CastleGateTowerEastSlit',Vector3.new(0.5,2.8,0.7),CFrame.new(10,13,-32.6),Enum.Material.Glass,GL)
P('CastleGateTowerEastBanner',Vector3.new(2.2,7,0.35),CFrame.new(10,10.5,-32.2),Enum.Material.Fabric,RD)
report('corner tower SE + gate towers')
end)

run_chunk(30, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local DK=Color3.fromRGB(210,204,194)
local GL=Color3.fromRGB(120,164,188)
P('CastleKeepPlinth',Vector3.new(24,2.4,24),CFrame.new(-2,1.2,-8),Enum.Material.Granite,DK)
P('CastleKeepBody',Vector3.new(21,30,21),CFrame.new(-2,17.4,-8),Enum.Material.Limestone,ST)
P('CastleKeepStringA',Vector3.new(21.8,1.2,21.8),CFrame.new(-2,14.4,-8),Enum.Material.Cobblestone,Color3.fromRGB(224,218,208))
P('CastleKeepStringB',Vector3.new(21.8,1.2,21.8),CFrame.new(-2,23.4,-8),Enum.Material.Cobblestone,Color3.fromRGB(224,218,208))
P('CastleKeepCornice',Vector3.new(23,1.4,23),CFrame.new(-2,31.5,-8),Enum.Material.Cobblestone,Color3.fromRGB(226,220,210))
for i=1,4 do
 P('CastleKeepCorner'..i,Vector3.new(3.2,34,3.2),CFrame.new(-2+(i%2==0 and 10 or -10),18,-8+(i<=2 and -10 or 10)),Enum.Material.Limestone,ST)
end
for i=1,3 do
 P('CastleKeepWindowE'..i,Vector3.new(0.5,3,1.6),CFrame.new(8.6,20,-12+i*4),Enum.Material.Glass,GL)
 P('CastleKeepWindowW'..i,Vector3.new(0.5,3,1.6),CFrame.new(-12.6,20,-12+i*4),Enum.Material.Glass,GL)
 P('CastleKeepWindowN'..i,Vector3.new(1.6,3,0.5),CFrame.new(-6+i*4,20,-18.6),Enum.Material.Glass,GL)
end
P('CastleKeepDoorFrame',Vector3.new(6,8,1.2),CFrame.new(-2,5.2,-18.9),Enum.Material.Granite,DK)
P('CastleKeepDoor',Vector3.new(4.6,7,0.5),CFrame.new(-2,3.5,-18.4),Enum.Material.Wood,Color3.fromRGB(112,76,48))
P('CastleKeepDoorIron',Vector3.new(4.8,0.4,0.7),CFrame.new(-2,4.6,-18.2),Enum.Material.CorrodedMetal,Color3.fromRGB(196,186,172))
report('keep')
end)

run_chunk(31, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(226,222,212)
local ang={45,135,225,315}
local ox={1,1,-1,-1}
local oz={-1,1,1,-1}
for i=1,4 do
 P('CastleKeepBattlementN'..i,Vector3.new(4.2,2.6,3),CFrame.new(-8.5+i*4.3,33.4,-18.5),Enum.Material.Cobblestone,ST)
 P('CastleKeepBattlementS'..i,Vector3.new(4.2,2.6,3),CFrame.new(-8.5+i*4.3,33.4,2.5),Enum.Material.Cobblestone,ST)
 P('CastleKeepBattlementW'..i,Vector3.new(3,2.6,4.2),CFrame.new(-12.5,33.4,-8.5+i*4.3),Enum.Material.Cobblestone,ST)
 P('CastleKeepBattlementE'..i,Vector3.new(3,2.6,4.2),CFrame.new(8.5,33.4,-8.5+i*4.3),Enum.Material.Cobblestone,ST)
end
for q=1,4 do
 P('CastleKeepConeQ'..q,Vector3.new(13,7,13),CFrame.new(-2+ox[q]*6.5,35.2,-8+oz[q]*6.5)*CFrame.Angles(0,math.rad(ang[q]),0),Enum.Material.Slate,Color3.fromRGB(202,206,214),Enum.PartType.CornerWedge)
end
P('CastleKeepFinial',Vector3.new(2.6,2.6,2.6),CFrame.new(-2,40,-8),Enum.Material.CorrodedMetal,Color3.fromRGB(216,210,200),Enum.PartType.Ball)
P('CastleKeepSpire',Vector3.new(0.4,3.4,0.4),CFrame.new(-2,43,-8),Enum.Material.Metal,Color3.fromRGB(200,198,196),Enum.PartType.Cylinder)
report('keep roof')
end)

run_chunk(32, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local WO=Color3.fromRGB(184,138,88)
local DK=Color3.fromRGB(208,202,192)
-- west bailey lean-to, flagpoles on the wall walk
P('CastleBaileyPost1',Vector3.new(0.6,5,0.6),CFrame.new(-31,15.8,-14),Enum.Material.Wood,WO)
P('CastleBaileyPost2',Vector3.new(0.6,5,0.6),CFrame.new(-31,15.8,-8),Enum.Material.Wood,WO)
P('CastleBaileyRoof',Vector3.new(6,0.5,7.4),CFrame.new(-29.2,18.2,-11)*CFrame.Angles(0,0,math.rad(-34)),Enum.Material.ClayRoofTiles,Color3.fromRGB(226,170,142))
P('CastleBaileyWall',Vector3.new(0.5,3.2,7),CFrame.new(-32.2,1.6,-11),Enum.Material.WoodPlanks,Color3.fromRGB(196,152,104))
for i=1,3 do
 P('CastleWallFlagpole'..i,Vector3.new(0.35,9,0.35),CFrame.new(-32+math.floor(i/2)*64,18.4,-27),Enum.Material.Wood,Color3.fromRGB(120,90,62),Enum.PartType.Cylinder)
 P('CastleWallFlag'..i,Vector3.new(0.2,4,5.5),CFrame.new(-32+math.floor(i/2)*64+3,21.2,-27),Enum.Material.Fabric,Color3.fromRGB(58,84,142))
end
-- gate braziers flanking the road
for i=1,2 do
 P('CastleBrazierBowl'..i,Vector3.new(2.6,2,2.6),CFrame.new(-8+i*16,3.6,-33),Enum.Material.CorrodedMetal,DK,Enum.PartType.Cylinder)
 P('CastleBrazierLeg'..i,Vector3.new(0.6,3,0.6),CFrame.new(-8+i*16,1.5,-33),Enum.Material.CorrodedMetal,DK)
 P('CastleBrazierCoal'..i,Vector3.new(2,2,2),CFrame.new(-8+i*16,5.2,-33),Enum.Material.Neon,Color3.fromRGB(255,164,64),Enum.PartType.Ball)
end
report('bailey + flags + braziers')
end)

run_chunk(33, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local n=0
for _,c in ipairs(workspace:GetChildren()) do if c.Name:sub(1,6)=='Castle' then n=n+1 end end
report('parts named Castle*: '..n)
end)

run_chunk(34, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local DK=Color3.fromRGB(210,204,194)
local WO=Color3.fromRGB(182,136,86)
P('CastleGuardhouseBody',Vector3.new(11,6,8),CFrame.new(-19,3,21),Enum.Material.Plaster,ST)
P('CastleGuardhouseBase',Vector3.new(11.7,1.2,8.7),CFrame.new(-19,0.6,21),Enum.Material.Granite,DK)
P('CastleGuardhouseRoofA',Vector3.new(6.4,0.8,9.4),CFrame.new(-21.6,7.3,21)*CFrame.Angles(0,0,math.rad(30)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleGuardhouseRoofB',Vector3.new(6.4,0.8,9.4),CFrame.new(-16.4,7.3,21)*CFrame.Angles(0,0,math.rad(-30)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleGuardhouseRidge',Vector3.new(1,1,9.7),CFrame.new(-19,9.3,21),Enum.Material.Wood,WO)
P('CastleGuardhouseDoor',Vector3.new(0.4,3.2,1.9),CFrame.new(-13.4,1.6,21),Enum.Material.Wood,Color3.fromRGB(148,102,62))
P('CastleGuardhouseWindow',Vector3.new(0.4,1.5,1.3),CFrame.new(-13.7,4.8,18.4),Enum.Material.Glass,Color3.fromRGB(122,166,190))
P('CastleGuardhouseLamp',Vector3.new(1.2,1.6,1.2),CFrame.new(-13.1,5.3,21),Enum.Material.Neon,Color3.fromRGB(255,180,90),Enum.PartType.Ball)
P('CastleGuardhouseLampCap',Vector3.new(1.9,0.4,1.9),CFrame.new(-13.1,6.3,21),Enum.Material.CorrodedMetal,DK)
P('CastleHayStack1',Vector3.new(3.7,3.7,3.7),CFrame.new(-11.6,1.85,24.6)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Fabric,Color3.fromRGB(216,192,120),Enum.PartType.Cylinder)
P('CastleHayStack2',Vector3.new(3,3,3),CFrame.new(-11.6,5.2,24.6)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Fabric,Color3.fromRGB(220,198,128),Enum.PartType.Cylinder)
P('CastleTroughBasin',Vector3.new(3.4,1.3,1.5),CFrame.new(-13.4,0.65,17.6),Enum.Material.WoodPlanks,Color3.fromRGB(192,148,100))
P('CastleTroughLeg1',Vector3.new(0.5,0.6,0.5),CFrame.new(-14.7,0.3,17.6),Enum.Material.Wood,WO)
P('CastleTroughLeg2',Vector3.new(0.5,0.6,0.5),CFrame.new(-12.1,0.3,17.6),Enum.Material.Wood,WO)
P('CastleLadderRung1',Vector3.new(2.4,0.3,0.3),CFrame.new(-19,2.6,16.6),Enum.Material.Wood,Color3.fromRGB(168,124,78))
P('CastleLadderRung2',Vector3.new(2.4,0.3,0.3),CFrame.new(-19,4.4,16.6),Enum.Material.Wood,Color3.fromRGB(168,124,78))
P('CastleLadderRung3',Vector3.new(2.4,0.3,0.3),CFrame.new(-19,6.2,16.6),Enum.Material.Wood,Color3.fromRGB(168,124,78))
P('CastleGuardBarrelA',Vector3.new(2.9,2.9,2.9),CFrame.new(-12.2,1.45,15.4),Enum.Material.Wood,Color3.fromRGB(170,126,78),Enum.PartType.Cylinder)
P('CastleGuardBarrelB',Vector3.new(2.5,2.5,2.5),CFrame.new(-12.2,4.1,15.4),Enum.Material.Wood,Color3.fromRGB(176,132,84),Enum.PartType.Cylinder)
report('south guardhouse + hay + ladder')
end)

run_chunk(35, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(210,204,194)
local WO=Color3.fromRGB(178,132,84)
local LEAF=Color3.fromRGB(206,228,176)
-- lumber yard SE
for i=1,4 do
 P('CastleLogPile'..i,Vector3.new(9,1.4,1.4),CFrame.new(-48,0.7+i*1.5,-24),Enum.Material.Wood,Color3.fromRGB(184,138,86),Enum.PartType.Cylinder)
end
P('CastleLogEndPost',Vector3.new(0.8,6.5,3.4),CFrame.new(-52.9,3.25,-24),Enum.Material.Wood,Color3.fromRGB(160,118,74))
P('CastleLogEndPost2',Vector3.new(0.8,6.5,3.4),CFrame.new(-43.1,3.25,-24),Enum.Material.Wood,Color3.fromRGB(160,118,74))
P('CastleLumberShedBody',Vector3.new(10,4.6,8),CFrame.new(-40,2.3,-14)*CFrame.Angles(0,math.rad(8),0),Enum.Material.WoodPlanks,Color3.fromRGB(194,150,102))
P('CastleLumberShedRoof',Vector3.new(11,0.8,9),CFrame.new(-40,5.2,-14)*CFrame.Angles(0,math.rad(8),math.rad(14)),Enum.Material.RoofShingles,Color3.fromRGB(206,200,200))
-- pond east
P('CastlePondRim',Vector3.new(19,1.1,19),CFrame.new(48,0.55,-52)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Cobblestone,DK,Enum.PartType.Cylinder)
P('CastlePondWater',Vector3.new(15.4,0.4,15.4),CFrame.new(48,1.0,-52)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Glass,Color3.fromRGB(108,150,176),Enum.PartType.Cylinder)
P('CastlePondReed1',Vector3.new(0.3,2.6,0.3),CFrame.new(41.5,2.2,-46),Enum.Material.Wood,Color3.fromRGB(176,148,86))
P('CastlePondReed2',Vector3.new(0.3,3.1,0.3),CFrame.new(42.6,2.4,-47.4),Enum.Material.Wood,Color3.fromRGB(176,148,86))
P('CastlePondReed3',Vector3.new(0.3,2.8,0.3),CFrame.new(54.2,2.3,-57.6),Enum.Material.Wood,Color3.fromRGB(176,148,86))
-- wayside shrine
P('CastleShrineBase',Vector3.new(7,1.2,7),CFrame.new(-58,0.6,-8),Enum.Material.Limestone,DK)
P('CastleShrineBody',Vector3.new(5.4,4.4,5.4),CFrame.new(-58,3.4,-8),Enum.Material.Sandstone,Color3.fromRGB(226,220,208))
P('CastleShrineRoof',Vector3.new(6.6,3.6,6.6),CFrame.new(-58,7.4,-8)*CFrame.Angles(0,math.rad(45),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(224,170,142),Enum.PartType.Wedge)
P('CastleShrineFinial',Vector3.new(1.4,1.4,1.4),CFrame.new(-58,10,-8),Enum.Material.Marble,Color3.fromRGB(228,226,222),Enum.PartType.Ball)
-- ruined tower west
P('CastleRuinBase',Vector3.new(11,1.4,11),CFrame.new(-58,0.7,32),Enum.Material.Rock,Color3.fromRGB(200,198,190))
P('CastleRuinWallA',Vector3.new(11,9,3),CFrame.new(-58,6.2,36.5),Enum.Material.Rock,Color3.fromRGB(202,200,192))
P('CastleRuinWallB',Vector3.new(3,7.5,9),CFrame.new(-62.5,5.1,32.5),Enum.Material.Rock,Color3.fromRGB(200,198,190))
P('CastleRuinFallen',Vector3.new(4.2,2.4,4.2),CFrame.new(-52,1.2,34)*CFrame.Angles(0,math.rad(23),0),Enum.Material.Rock,Color3.fromRGB(204,202,194),Enum.PartType.Ball)
-- extra trees
local T={{56,10,1.05,0.2},{58,26,0.95,0.5},{36,-60,1.15,0.1},{-8,-62,1.05,0.35},{-56,4,1.2,0.15},{58,62,1,0.4},{-60,60,0.9,0},{-62,-40,1.1,0.25}}
for i=1,8 do
 local x,z,s,r=T[i][1],T[i][2],T[i][3],T[i][4]
 P('CastleTreeTrunkX'..i,Vector3.new(1.3*s,6.6*s,1.3*s),CFrame.new(x,3.3*s,z),Enum.Material.Wood,Color3.fromRGB(190,144,88),Enum.PartType.Cylinder)
 P('CastleTreeCanopyX'..i,Vector3.new(6.6*s,5.8*s,6.6*s),CFrame.new(x,8*s,z),Enum.Material.LeafyGrass,LEAF,Enum.PartType.Ball)
 P('CastleTreeTopX'..i,Vector3.new(4.2*s,3.8*s,4.2*s),CFrame.new(x+math.sin(r),11.4*s,z+math.cos(r)),Enum.Material.LeafyGrass,Color3.fromRGB(212,232,182),Enum.PartType.Ball)
end
local n=0 local mats={} for _,c in ipairs(workspace:GetChildren()) do if c.Name:sub(1,6)=='Castle' then n=n+1 mats[c.Material.Name]=true end end local m=0 for _ in pairs(mats) do m=m+1 end
report('outer world; total Castle parts='..n..' materials='..m)
end)

run_chunk(36, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local DK=Color3.fromRGB(210,204,194)
-- conical roofs on the four corner towers (CornerWedge quadrants)
local T={{'NW',-31.5,-27},{'NE',31.5,-27},{'SE',31.5,27},{'SW',-31.5,27}}
for i=1,4 do
 local b=T[i][1] local x=T[i][2] local z=T[i][3]
 for q=1,4 do
  local ang={45,135,225,315}
  local ox={1,1,-1,-1}
  local oz={-1,1,1,-1}
  P('CastleTower'..b..'ConeQ'..q,Vector3.new(9.5,5.2,9.5),CFrame.new(x+ox[q]*4.8,24.4,z+oz[q]*4.8)*CFrame.Angles(0,math.rad(ang[q]),0),Enum.Material.Slate,Color3.fromRGB(202,206,214),Enum.PartType.CornerWedge)
 end
 P('CastleTower'..b..'Finial',Vector3.new(2.2,2.2,2.2),CFrame.new(x,28.4,z),Enum.Material.CorrodedMetal,Color3.fromRGB(216,210,200),Enum.PartType.Ball)
 P('CastleTower'..b..'Mast',Vector3.new(0.3,3,0.3),CFrame.new(x,30.9,z),Enum.Material.Metal,Color3.fromRGB(200,198,196),Enum.PartType.Cylinder)
 P('CastleTower'..b..'Pennant',Vector3.new(0.2,1.4,3.4),CFrame.new(x,32.2,z+1.7),Enum.Material.Fabric,Color3.fromRGB(58,84,142))
end
report('tower cone roofs')
end)

run_chunk(37, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(210,204,194)
local WO=Color3.fromRGB(180,134,86)
-- gate tower pitched roofs + hoisting beams
P('CastleGateTowerWestRoofA',Vector3.new(7,0.8,12.6),CFrame.new(-13.2,25.4,-27)*CFrame.Angles(0,0,math.rad(28)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleGateTowerWestRoofB',Vector3.new(7,0.8,12.6),CFrame.new(-6.8,25.4,-27)*CFrame.Angles(0,0,math.rad(-28)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleGateTowerWestRidge',Vector3.new(1,1,12.8),CFrame.new(-10,27.6,-27),Enum.Material.Wood,WO)
P('CastleGateTowerEastRoofA',Vector3.new(7,0.8,12.6),CFrame.new(6.8,25.4,-27)*CFrame.Angles(0,0,math.rad(28)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleGateTowerEastRoofB',Vector3.new(7,0.8,12.6),CFrame.new(13.2,25.4,-27)*CFrame.Angles(0,0,math.rad(-28)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleGateTowerEastRidge',Vector3.new(1,1,12.8),CFrame.new(10,27.6,-27),Enum.Material.Wood,WO)
P('CastleGateHoistBeam',Vector3.new(0.7,0.7,9),CFrame.new(0,11.6,-30.4),Enum.Material.Wood,Color3.fromRGB(150,112,70))
P('CastleGateHoistRope1',Vector3.new(0.2,4.4,0.2),CFrame.new(-4.6,9.4,-31.6),Enum.Material.Fabric,Color3.fromRGB(206,196,172))
P('CastleGateHoistRope2',Vector3.new(0.2,4.4,0.2),CFrame.new(4.6,9.4,-31.6),Enum.Material.Fabric,Color3.fromRGB(206,196,172))
-- curtain-wall stair up to the north wall-walk beside the gate
for i=1,7 do
 P('CastleGateStair'..i,Vector3.new(2.4,1,2.4),CFrame.new(-13.6+i*1.9,0.5+i*1.35,-22.6),Enum.Material.Limestone,DK)
end
P('CastleGateStairTop',Vector3.new(2.6,1,6),CFrame.new(0.6,10.2,-24.4),Enum.Material.Limestone,DK)
report('gate tower roofs + hoist')
end)

run_chunk(38, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local ST=Color3.fromRGB(228,224,212)
local WO=Color3.fromRGB(180,134,86)
-- herb garden beds + apple trees inside the east bailey
for i=1,4 do
 P('CastleHerbBed'..i,Vector3.new(3.4,1,5),CFrame.new(8+i*4.6,0.5,20),Enum.Material.Wood,WO)
 P('CastleHerbGreen'..i,Vector3.new(2.6,0.9,4.2),CFrame.new(8+i*4.6,1.4,20),Enum.Material.LeafyGrass,Color3.fromRGB(208,232,176))
end
P('CastleAppleTrunkA',Vector3.new(1.2,5.5,1.2),CFrame.new(-12,2.75,6)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,Color3.fromRGB(186,140,86),Enum.PartType.Cylinder)
P('CastleAppleCrownA',Vector3.new(5.4,4.6,5.4),CFrame.new(-12,6.4,6),Enum.Material.LeafyGrass,Color3.fromRGB(204,228,172),Enum.PartType.Ball)
P('CastleAppleTrunkB',Vector3.new(1.2,5,1.2),CFrame.new(-18,2.5,-2)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Wood,Color3.fromRGB(186,140,86),Enum.PartType.Cylinder)
P('CastleAppleCrownB',Vector3.new(5,4.2,5),CFrame.new(-18,6, -2),Enum.Material.LeafyGrass,Color3.fromRGB(206,230,176),Enum.PartType.Ball)
-- dovecote tower in the SE bailey
P('CastleDovecoteBody',Vector3.new(7,7,7),CFrame.new(27,3.5,17),Enum.Material.Plaster,ST)
P('CastleDovecoteRoof',Vector3.new(7.8,3.4,7.8),CFrame.new(27,8.7,17)*CFrame.Angles(0,math.rad(45),0),Enum.Material.ClayRoofTiles,Color3.fromRGB(226,172,144),Enum.PartType.Wedge)
P('CastleDovecoteCap',Vector3.new(1.3,1.3,1.3),CFrame.new(27,11.5,17),Enum.Material.Marble,Color3.fromRGB(228,226,222),Enum.PartType.Ball)
P('CastleDovecoteHole1',Vector3.new(0.4,0.7,0.7),CFrame.new(27,5.4,13.4),Enum.Material.Wood,Color3.fromRGB(120,88,58))
P('CastleDovecoteHole2',Vector3.new(0.4,0.7,0.7),CFrame.new(27,5.4,20.6),Enum.Material.Wood,Color3.fromRGB(120,88,58))
P('CastleDovecoteLadder',Vector3.new(0.3,7,0.3),CFrame.new(29.6,3.5,13.9),Enum.Material.Wood,Color3.fromRGB(168,124,78),Enum.PartType.Cylinder)
report('herbs, orchard, dovecote')
end)

run_chunk(39, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(210,204,194)
local WO=Color3.fromRGB(180,134,86)
local GL=Color3.fromRGB(122,166,190)
-- west gatehouse landing inside the south wall + inner bailey
P('CastleInnerBaileyWall',Vector3.new(0.5,3.4,9),CFrame.new(-31.7,1.7,-24),Enum.Material.Cobblestone,Color3.fromRGB(224,220,210))
P('CastleInnerBaileyCoping',Vector3.new(0.9,0.5,9.4),CFrame.new(-31.7,3.6,-24),Enum.Material.Slate,Color3.fromRGB(202,206,212))
-- pig sty / animal pens SE bailey
for i=1,3 do
 P('CastlePenPost'..i,Vector3.new(0.45,3,0.45),CFrame.new(12+i*4,1.5,13),Enum.Material.Wood,WO)
end
P('CastlePenRail',Vector3.new(12.4,0.3,0.3),CFrame.new(16,2.2,13),Enum.Material.Wood,Color3.fromRGB(170,126,80),Enum.PartType.Cylinder)
P('CastlePenHut',Vector3.new(5,2.6,4),CFrame.new(19,1.3,16),Enum.Material.WoodPlanks,Color3.fromRGB(188,146,100))
P('CastlePenHutRoof',Vector3.new(5.6,0.5,4.6),CFrame.new(19,2.9,16)*CFrame.Angles(0,0,math.rad(14)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastlePenHog',Vector3.new(2.2,1.4,1.4),CFrame.new(15.6,1.1,10.6),Enum.Material.Fabric,Color3.fromRGB(206,178,166),Enum.PartType.Ball)
P('CastlePenHog2',Vector3.new(2,1.3,1.3),CFrame.new(18.4,1,10.2)*CFrame.Angles(0,math.rad(20),0),Enum.Material.Fabric,Color3.fromRGB(198,170,160),Enum.PartType.Ball)
-- straw bales NE bailey
for i=1,3 do
 P('CastleStrawBale'..i,Vector3.new(3.2,2.2,2.2),CFrame.new(10+i*3.6,1.1,4+i*1.4)*CFrame.Angles(0,math.rad(i*11),0),Enum.Material.Fabric,Color3.fromRGB(220,196,124),Enum.PartType.Cylinder)
end
-- paving detail: courtyard stones + kerb along the inner road
for i=1,6 do
 P('CastlePaveSlab'..i,Vector3.new(3.4,0.5,3.4),CFrame.new(-7+i*3.2,0.42,-8+math.sin(i)*2.2),Enum.Material.Pebble,Color3.fromRGB(224,220,210))
end
P('CastleKerbW',Vector3.new(1,0.7,30),CFrame.new(-3.6,0.35,-2),Enum.Material.Limestone,DK)
P('CastleKerbE',Vector3.new(1,0.7,30),CFrame.new(3.6,0.35,-2),Enum.Material.Limestone,DK)
-- roofed well-side bucket stand + sacks
P('CastleSack1',Vector3.new(1.6,1.8,1.6),CFrame.new(26.4,0.9,-19.4),Enum.Material.Fabric,Color3.fromRGB(208,196,164),Enum.PartType.Ball)
P('CastleSack2',Vector3.new(1.5,1.6,1.5),CFrame.new(28.2,0.8,-18.6)*CFrame.Angles(0,math.rad(30),0),Enum.Material.Fabric,Color3.fromRGB(212,200,168),Enum.PartType.Ball)
report('bailey detail: pens, bales, paving')
end)

run_chunk(40, function()
local function P(n,s,c,m,col,sh)
 local p=Instance.new('Part')
 p.Name=n p.Size=s p.CFrame=c p.Material=m
 if col then p.Color=col end
 if sh then p.Shape=sh end
 p.Anchored=true p.Parent=workspace
 return p
end
local DK=Color3.fromRGB(210,204,194)
local WO=Color3.fromRGB(180,134,86)
local ST=Color3.fromRGB(228,224,212)
-- NE gate in the east curtain: barbican + flanking drum towers
P('CastleEastBarbicanBody',Vector3.new(8,15,9),CFrame.new(36,7.5,4)*CFrame.Angles(0,0,math.rad(90)),Enum.Material.Cobblestone,ST)
P('CastleEastBarbicanLintel',Vector3.new(9,2.4,10),CFrame.new(36,14.6,4),Enum.Material.Granite,Color3.fromRGB(216,210,200))
P('CastleEastBarbicanDoorBeam',Vector3.new(9.4,0.8,1.4),CFrame.new(36,1.4,4),Enum.Material.Wood,Color3.fromRGB(148,104,64))
P('CastleEastBarbicanRoofA',Vector3.new(5.6,0.7,9.6),CFrame.new(34,17.4,4)*CFrame.Angles(0,0,math.rad(30)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleEastBarbicanRoofB',Vector3.new(5.6,0.7,9.6),CFrame.new(38,17.4,4)*CFrame.Angles(0,0,math.rad(-30)),Enum.Material.RoofShingles,Color3.fromRGB(208,204,204))
P('CastleEastBarbicanRidge',Vector3.new(0.9,0.9,9.8),CFrame.new(36,19.4,4),Enum.Material.Wood,WO)
-- postern stair from courtyard down to the east sally port
for i=1,5 do
 P('CastlePosternStair'..i,Vector3.new(2.2,0.9,2.2),CFrame.new(28.4,0.45+i*0.85,14-i*2.4),Enum.Material.Limestone,DK)
end
P('CastlePosternLanding',Vector3.new(2.4,0.9,4),CFrame.new(28.4,5.2,3.4),Enum.Material.Limestone,DK)
-- gate lamps hanging under the north lintel
for i=1,2 do
 P('CastleGateLampChain'..i,Vector3.new(0.14,2.2,0.14),CFrame.new(-2.4+i*4.8,12.4,-27.4),Enum.Material.CorrodedMetal,DK)
 P('CastleGateLamp'..i,Vector3.new(1.3,1.6,1.3),CFrame.new(-2.4+i*4.8,10.6,-27.4),Enum.Material.Neon,Color3.fromRGB(255,176,80),Enum.PartType.Ball)
 P('CastleGateLampCage'..i,Vector3.new(1.9,0.4,1.9),CFrame.new(-2.4+i*4.8,11.6,-27.4),Enum.Material.Metal,Color3.fromRGB(206,204,202))
end
-- doormat / straw at the main gate
P('CastleGateStrawMat',Vector3.new(7,0.3,4),CFrame.new(0,0.3,-24.4),Enum.Material.Fabric,Color3.fromRGB(218,196,128))
report('barbican, postern, gate lamps')
end)

run_chunk(41, function()
local n=0 local mats={} local shp={} for _,c in ipairs(workspace:GetChildren()) do if c.Name:sub(1,6)=='Castle' then n=n+1 mats[c.Material.Name]=true shp[c.Shape.Name]=true end end local m=0 for _ in pairs(mats) do m=m+1 end local s=0 for _ in pairs(shp) do s=s+1 end report('Castle parts='..n..' distinct materials='..m..' distinct shapes='..s)
end)

run_chunk(42, function()
local n=0 local mats={} local shp={} for _,c in ipairs(workspace:GetChildren()) do if c.Name:sub(1,6)=='Castle' then n=n+1 mats[c.Material.Name]=true shp[c.Shape.Name]=true end end local m=0 for _ in pairs(mats) do m=m+1 end local s=0 for _ in pairs(shp) do s=s+1 end report('FINAL Castle parts='..n..' materials='..m..' shapes='..s)
end)
