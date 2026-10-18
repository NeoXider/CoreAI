-- Model-authored execute_lua calls in original order.
-- Model: DeepSeek V4.1 Flash 1017; scenario: g6_free_build; calls: 42
-- pcall keeps later calls running after a failed section, matching the benchmark.
local function run_chunk(index, body)
    local ok, err = pcall(body)
    if not ok then print('G6 chunk ' .. index .. ' failed: ' .. tostring(err)) end
end

run_chunk(1, function()
local info = {}
table.insert(info, tostring(#workspace:GetDescendants()))
local found = coreai_world_find("Castle")
table.insert(info, "found:" .. tostring(#found))
return table.concat(info, " | ")
end)

run_chunk(2, function()
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part')
  p.Name = n
  p.Size = s
  p.CFrame = c
  p.Material = m
  p.Color = col or Color3.fromRGB(228,226,218)
  p.Shape = sh or Enum.PartType.Block
  p.Anchored = true
  p.CanCollide = true
  p.Parent = workspace
  return p
end
local B = Enum.PartType.Block
local GR = Color3.fromRGB(202,206,190)
P('CastleGround', Vector3.new(128,1,128), CFrame.new(0,-0.5,0), Enum.Material.Grass, GR, B)
P('CastleGroundApron', Vector3.new(36,0.5,12), CFrame.new(0,0.25,36), Enum.Material.Mud, Color3.fromRGB(206,200,186), B)
P('CastleRoadMain', Vector3.new(11,0.5,26), CFrame.new(0,0.25,49), Enum.Material.Pavement, Color3.fromRGB(232,230,222), B)
P('CastleRoadCurbW', Vector3.new(0.9,0.9,26), CFrame.new(-5.8,0.45,49), Enum.Material.Cobblestone, Color3.fromRGB(220,216,206), B)
P('CastleRoadCurbE', Vector3.new(0.9,0.9,26), CFrame.new(5.8,0.45,49), Enum.Material.Cobblestone, Color3.fromRGB(216,212,202), B)
P('CastleGardenSoil', Vector3.new(16,0.5,11), CFrame.new(-17,0.25,46), Enum.Material.Mud, Color3.fromRGB(200,192,176), B)
P('CastleGardenHedgeA', Vector3.new(16,1.3,1.3), CFrame.new(-17,0.65,40.6), Enum.Material.LeafyGrass, Color3.fromRGB(214,224,198), B)
P('CastleGardenHedgeB', Vector3.new(1.3,1.3,11), CFrame.new(-25,0.65,46), Enum.Material.LeafyGrass, Color3.fromRGB(210,220,194), B)
P('CastlePondWater', Vector3.new(16,0.6,10), CFrame.new(20,0.3,48), Enum.Material.Glass, Color3.fromRGB(96,158,196), B)
P('CastlePondRimN', Vector3.new(18,0.9,1.2), CFrame.new(20,0.45,53.1), Enum.Material.Cobblestone, Color3.fromRGB(218,214,204), B)
P('CastlePondRimS', Vector3.new(18,0.9,1.2), CFrame.new(20,0.45,42.9), Enum.Material.Cobblestone, Color3.fromRGB(222,218,208), B)
P('CastleBoulderA', Vector3.new(3,3,3), CFrame.new(-40,1.5,40), Enum.Material.Rock, Color3.fromRGB(220,216,208), Enum.PartType.Ball)
P('CastleBoulderB', Vector3.new(2.4,2.4,2.4), CFrame.new(-44,1.2,45), Enum.Material.Basalt, Color3.fromRGB(210,206,200), Enum.PartType.Ball)
P('CastleBoulderC', Vector3.new(5,5,5), CFrame.new(44,2.5,44), Enum.Material.Granite, Color3.fromRGB(224,220,212), Enum.PartType.Ball)
P('CastleBoulderD', Vector3.new(3.6,3.6,3.6), CFrame.new(48,1.8,39), Enum.Material.Rock, Color3.fromRGB(214,210,202), Enum.PartType.Ball)
report('ground/roads/base rocks done')
end)

run_chunk(3, function()
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part')
  p.Name = n
  p.Size = s
  p.CFrame = c
  p.Material = m
  p.Color = col or Color3.fromRGB(228,226,218)
  p.Shape = sh or Enum.PartType.Block
  p.Anchored = true
  p.Parent = workspace
  return p
end
local B = Enum.PartType.Block
local LS = Color3.fromRGB(232,230,222)
-- North curtain (outer face z=-31), broken by gate x -9..9
P('CastleCurtainNW', Vector3.new(31,10,3), CFrame.new(-24.5,5,-29.5), Enum.Material.Limestone, LS, B)
P('CastleCurtainNE', Vector3.new(31,10,3), CFrame.new(24.5,5,-29.5), Enum.Material.Limestone, LS, B)
P('CastleCurtainW', Vector3.new(3,10,56), CFrame.new(-29.5,5,0), Enum.Material.Limestone, LS, B)
P('CastleCurtainE', Vector3.new(3,10,56), CFrame.new(29.5,5,0), Enum.Material.Limestone, LS, B)
P('CastleCurtainSW', Vector3.new(31,10,3), CFrame.new(-24.5,5,29.5), Enum.Material.Limestone, LS, B)
P('CastleCurtainSE', Vector3.new(31,10,3), CFrame.new(24.5,5,29.5), Enum.Material.Limestone, LS, B)
-- north gate jambs (inner faces at x -8..8 -> gate 16 wide)
P('CastleGateJambW', Vector3.new(1.4,12,3.4), CFrame.new(-8.7,6,-29.5), Enum.Material.Cobblestone, Color3.fromRGB(226,222,212), B)
P('CastleGateJambE', Vector3.new(1.4,12,3.4), CFrame.new(8.7,6,-29.5), Enum.Material.Cobblestone, Color3.fromRGB(222,218,208), B)
-- wall coping slabs on top of curtain
P('CastleCopingNW', Vector3.new(31,0.7,3.6), CFrame.new(-24.5,10.05,-29.5), Enum.Material.Slate, Color3.fromRGB(224,222,216), B)
P('CastleCopingNE', Vector3.new(31,0.7,3.6), CFrame.new(24.5,10.05,-29.5), Enum.Material.Slate, Color3.fromRGB(220,218,212), B)
P('CastleCopingS', Vector3.new(62,0.7,3.6), CFrame.new(0,10.05,29.5), Enum.Material.Slate, Color3.fromRGB(226,224,218), B)
P('CastleCopingW', Vector3.new(3.6,0.7,56), CFrame.new(-29.5,10.05,0), Enum.Material.Slate, Color3.fromRGB(222,220,214), B)
P('CastleCopingE', Vector3.new(3.6,0.7,56), CFrame.new(29.5,10.05,0), Enum.Material.Slate, Color3.fromRGB(218,216,210), B)
report('curtain walls done')
end)

run_chunk(4, function()
local p = Instance.new('Part') p.Name='CastleProbeW' p.Size=Vector3.new(10,10,10) p.CFrame=CFrame.new(0,60,0) p.Anchored=true p.Shape=Enum.PartType.Wedge p.Parent=workspace
local q = Instance.new('Part') q.Name='CastleProbeC' q.Size=Vector3.new(10,10,10) q.CFrame=CFrame.new(40,60,0) q.Anchored=true q.Shape=Enum.PartType.CornerWedge q.Parent=workspace
local out = {}
for _, zz in ipairs({-4,0,4}) do
  local r = workspace:Raycast(Vector3.new(0,80,zz), Vector3.new(0,-60,0))
  table.insert(out, "W z="..zz.." hitY="..(r and string.format("%.1f", r.Position.Y) or "nil"))
end
for _, v in ipairs({{-4,-4},{4,-4},{-4,4},{4,4},{0,0}}) do
  local r = workspace:Raycast(Vector3.new(40+v[1],80,v[2]), Vector3.new(0,-60,0))
  table.insert(out, "C x="..v[1].." z="..v[2].." hitY="..(r and string.format("%.1f", r.Position.Y) or "nil"))
end
p:Destroy() q:Destroy()
return table.concat(out, " | ")
end)

run_chunk(5, function()
local out = {}
local r0 = workspace:Raycast(Vector3.new(0,40,0), Vector3.new(0,-50,0))
table.insert(out, "ground hit=" .. (r0 and r0.Position.Y or "nil"))
local t = Instance.new('Part') t.Name='CastleProbeT' t.Size=Vector3.new(6,6,6) t.CFrame=CFrame.new(-50,30,0) t.Anchored=true t.Material=Enum.Material.Limestone t.Shape=Enum.PartType.Wedge t.Parent=workspace
local r1 = workspace:Raycast(Vector3.new(-50,60,-2.5), Vector3.new(0,-50,0))
local r2 = workspace:Raycast(Vector3.new(-50,60,2.5), Vector3.new(0,-50,0))
datetime = nil
table.insert(out, "wedge -Z side=" .. (r1 and string.format("%.1f", r1.Position.Y) or "nil"))
table.insert(out, "wedge +Z side=" .. (r2 and string.format("%.1f", r2.Position.Y) or "nil"))
local pos = coreai_world_pos('CastleProbeT')
table.insert(out, "pos=" .. tostring(pos))
return table.concat(out, " | ")
end)

run_chunk(6, function()
local t = Instance.new('Part') t.Name='CastleProbeT2' t.Size=Vector3.new(6,6,6) t.CFrame=CFrame.new(-50,30,0) t.Anchored=true t.CanCollide=true t.Material=Enum.Material.Limestone t.Shape=Enum.PartType.Wedge t.Parent=workspace
local a = Instance.new('Part') a.Name='CastleProbeA' a.Size=Vector3.new(2,0.2,2) a.CFrame=CFrame.new(-50,32.8,2.2) a.Anchored=true a.CanCollide=true a.Parent=workspace
local b = Instance.new('Part') b.Name='CastleProbeB' b.Size=Vector3.new(2,0.2,2) b.CFrame=CFrame.new(-50,32.8,-2.2) b.Anchored=true b.CanCollide=true b.Parent=workspace
local okA, resA = pcall(function() local tt = a:GetTouchingParts() local n={} for _,x in ipairs(tt) do table.insert(n,x.Name) end return table.concat(n,",") end)
local okB, resB = pcall(function() local tt = b:GetTouchingParts() local n={} for _,x in ipairs(tt) do table.insert(n,x.Name) end return table.concat(n,",") end)
a:Destroy() b:Destroy() t:Destroy()
return "+Z touch: " .. tostring(okA) .. " " .. tostring(resA) .. " || -Z touch: " .. tostring(okB) .. " " .. tostring(resB)
end)

run_chunk(7, function()
local t = Instance.new('Part') t.Name='CastleProbeT3' t.Size=Vector3.new(6,6,6) t.CFrame=CFrame.new(-50,30,0) t.Anchored=true t.CanCollide=true t.Material=Enum.Material.Limestone t.Shape=Enum.PartType.Wedge t.Parent=workspace
local a = Instance.new('Part') a.Name='CastleProbeA2' a.Size=Vector3.new(2,0.2,2) a.CFrame=CFrame.new(-50,32.8,2.2) a.Anchored=true a.CanCollide=true a.Parent=workspace
local b = Instance.new('Part') b.Name='CastleProbeB2' b.Size=Vector3.new(2,0.2,2) b.CFrame=CFrame.new(-50,32.8,-2.2) b.Anchored=true a.CanCollide=true b.Parent=workspace
local out = {}
local ok, res = pcall(function()
  local list = workspace:GetPartsInPart(a)
  local n = {} for _,x in ipairs(list) do table.insert(n, x.Name) end
  return table.concat(n, ",")
end)
table.insert(out, "GetPartsInPart A: " .. tostring(ok) .. " " .. tostring(res))
local ok2, res2 = pcall(function()
  local list = workspace:GetPartBoundsInBox(CFrame.new(-50,32.8,-2.2), Vector3.new(2,0.2,2))
  local n = {} for _,x in ipairs(list) do table.insert(n, x.Name) end
  return table.concat(n, ",")
end)
table.insert(out, "BoundsInBox B: " .. tostring(ok2) .. " " .. tostring(res2))
a:Destroy() b:Destroy() t:Destroy()
return table.concat(out, " || ")
end)

run_chunk(8, function()
local out = {}
local ok1, r1 = pcall(function() return workspace:Raycast(Vector3.new(0,40,0), Vector3.new(0,-50,0)) end)
table.insert(out, "plain: " .. tostring(ok1) .. " " .. tostring(r1))
local ok2, r2 = pcall(function()
  local pr = RaycastParams.new()
  pr.FilterType = Enum.RaycastFilterType.Exclude
  pr.FilterDescendantsInstances = {}
  return workspace:Raycast(Vector3.new(0.3,40,0.7), Vector3.new(0,-50,0), pr)
end)
table.insert(out, "params: " .. tostring(ok2) .. " " .. tostring(r2))
return table.concat(out, " || ")
end)

run_chunk(9, function()
local keys = {}
for k,v in pairs(_G) do table.insert(keys, k .. ":" .. type(v)) end
table.sort(keys)
return table.concat(keys, ", ")
end)

run_chunk(10, function()
local t = Instance.new('Part') t.Name='CastleProbeT4' t.Size=Vector3.new(6,6,6) t.CFrame=CFrame.new(-50,30,0) t.Anchored=true t.CanCollide=true t.Material=Enum.Material.Limestone t.Shape=Enum.PartType.Wedge t.Parent=workspace
local out = {}
local ok, r = pcall(function() return coreai_world_raycast({x=0,y=40,z=0}, {x=0,y=-50,z=0}) end)
table.insert(out, "ground: " .. tostring(ok) .. " " .. tostring(r))
local ok2, r2 = pcall(function() return coreai_world_raycast({x=-50,y=40,z=-3.5}, {x=0,y=-60,z=0}) end)
table.insert(out, "-Z side: " .. tostring(ok2) .. " " .. tostring(r2))
local ok3, r3 = pcall(function() return coreai_world_raycast({x=-50,y=40,z=3.5}, {x=0,y=-60,z=0}) end)
table.insert(out, "+Z side: " .. tostring(ok3) .. " " .. tostring(r3))
t:Destroy()
return table.concat(out, " || ")
end)

run_chunk(11, function()
local t = Instance.new('Part') t.Name='CastleProbeT5' t.Size=Vector3.new(6,6,6) t.CFrame=CFrame.new(-50,30,0) t.Anchored=true t.CanCollide=true t.Material=Enum.Material.Limestone t.Shape=Enum.PartType.Wedge t.Parent=workspace
local out = {}
local ok, r = pcall(function() return coreai_world_raycast(0,40,0, 0,-50,0) end)
table.insert(out, "ground: " .. tostring(ok) .. " " .. tostring(r))
local ok2, r2 = pcall(function() return coreai_world_raycast(-50,40,-3.5, 0,-60,0) end)
table.insert(out, "-Z side: " .. tostring(ok2) .. " " .. tostring(r2))
local ok3, r3 = pcall(function() return coreai_world_raycast(-50,40,3.5, 0,-60,0) end)
table.insert(out, "+Z side: " .. tostring(ok3) .. " " .. tostring(r3))
t:Destroy()
return table.concat(out, " || ")
end)

run_chunk(12, function()
local t = Instance.new('Part') t.Name='CastleProbeT6' t.Size=Vector3.new(8,8,8) t.CFrame=CFrame.new(-50,30,0) t.Anchored=true t.CanCollide=true t.Material=Enum.Material.Limestone t.Shape=Enum.PartType.Wedge t.Parent=workspace
local out = {}
local ok, g = pcall(function() return coreai_world_grid() end)
table.insert(out, "grid ok=" .. tostring(ok) .. " type=" .. type(g))
if ok and type(g) == 'table' then
  local i = 0
  for k,v in pairs(g) do i = i + 1; if i <= 12 then table.insert(out, tostring(k).."="..tostring(v)) end end
  table.insert(out, "count="..i)
end
local ok2, p2 = pcall(function() return coreai_world_pos('CastleProbeT6') end)
table.insert(out, "pos="..tostring(p2).." ok="..tostring(ok2))
t:Destroy()
return table.concat(out, " || ")
end)

run_chunk(13, function()
local function kill(n) local o = workspace:FindFirstChild(n) if o then o:Destroy() end end
for _, n in ipairs({'CastleCurtainNW','CastleCurtainNE','CastleCopingNW','CastleCopingNE','CastleGateJambW','CastleGateJambE'}) do kill(n) end
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block
local LS=Color3.fromRGB(230,228,220)
P('CastleCurtainNW', Vector3.new(22,10,3), CFrame.new(-20,5,-29.5), Enum.Material.Limestone, LS, B)
P('CastleCurtainNE', Vector3.new(22,10,3), CFrame.new(20,5,-29.5), Enum.Material.Limestone, Color3.fromRGB(226,224,216), B)
P('CastleCopingNW', Vector3.new(22,0.7,3.6), CFrame.new(-20,10.05,-29.5), Enum.Material.Slate, Color3.fromRGB(224,222,216), B)
P('CastleCopingNE', Vector3.new(22,0.7,3.6), CFrame.new(20,10.05,-29.5), Enum.Material.Slate, Color3.fromRGB(220,218,212), B)
local i=0
for _, x in ipairs({-29,-25.4,-21.8,-18.2,-14.6,-11}) do
  i=i+1
  P('CastleMerlonN'..i, Vector3.new(1.8,1.7,0.9), CFrame.new(x,11.25,-30.2), Enum.Material.Limestone, LS, B)
end
for _, x in ipairs({11,14.6,18.2,21.8,25.4,29}) do
  i=i+1
  P('CastleMerlonN'..i, Vector3.new(1.7,1.6,0.9), CFrame.new(x,11.2,-30.2), Enum.Material.Limestone, LS, B)
end
report('north wall rebuilt with parapet merlons')
end)

run_chunk(14, function()
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block
local LS=Color3.fromRGB(230,228,220)
-- courtyard campfire
P('CastleCampfireBase', Vector3.new(5,0.5,5), CFrame.new(4,0.25,19), Enum.Material.Rock, Color3.fromRGB(222,220,212), B)
P('CastleCampfireEmbers', Vector3.new(2.6,0.6,2.6), CFrame.new(4,0.45,19), Enum.Material.CrackedLava, nil, B)
P('CastleCampfireLogA', Vector3.new(2.8,0.7,0.8), CFrame.new(4,0.75,19)*CFrame.Angles(0,math.rad(20),0), Enum.Material.WoodPlanks, Color3.fromRGB(226,222,210), B)
P('CastleCampfireLogB', Vector3.new(2.8,0.7,0.8), CFrame.new(4,0.95,19)*CFrame.Angles(0,math.rad(-38),0), Enum.Material.WoodPlanks, Color3.fromRGB(222,216,204), B)
P('CastleCampfireFlameA', Vector3.new(1.1,1.7,1.1), CFrame.new(4,1.5,19)*CFrame.Angles(0,math.rad(15),math.rad(-8)), Enum.Material.Neon, Color3.fromRGB(255,120,40), Enum.PartType.Wedge)
P('CastleCampfireFlameB', Vector3.new(0.9,1.5,0.9), CFrame.new(4.2,2.0,18.9)*CFrame.Angles(math.rad(10),math.rad(40),math.rad(12)), Enum.Material.Neon, Color3.fromRGB(255,190,70), Enum.PartType.Wedge)
P('CastleCampfireSmoke', Vector3.new(2.2,2.2,2.2), CFrame.new(4.5,4.2,19.2), Enum.Material.ForceField, Color3.fromRGB(210,210,220), Enum.PartType.Ball)
-- market stall near fire
P('CastleStallPost3', Vector3.new(0.5,6.6,0.5), CFrame.new(16,3.3,-21), Enum.Material.Wood, Color3.fromRGB(228,224,214), B)
P('CastleStallPost4', Vector3.new(0.5,6.4,0.5), CFrame.new(16,3.2,21), Enum.Material.Wood, Color3.fromRGB(224,220,210), B)
P('CastleStallCanopy3', Vector3.new(1.8,0.6,44), CFrame.new(16.2,7.0,0)*CFrame.Angles(0,0,math.rad(-5)), Enum.Material.Fabric, Color3.fromRGB(160,60,60), B)
P('CastleStallCanopyFlap', Vector3.new(4.4,0.5,42.4), CFrame.new(17.8,6.2,0)*CFrame.Angles(0,0,math.rad(-32)), Enum.Material.Fabric, Color3.fromRGB(140,52,52), B)
P('CastleStallCounter', Vector3.new(1.6,1.2,40), CFrame.new(15.6,0.9,0), Enum.Material.WoodPlanks, Color3.fromRGB(230,226,216), B)
P('CastleStallSack1', Vector3.new(1.3,1.5,1.3), CFrame.new(15.4,1.9,-14), Enum.Material.Fabric, Color3.fromRGB(210,190,150), Enum.PartType.Ball)
P('CastleStallSack2', Vector3.new(1.2,1.4,1.2), CFrame.new(15.5,1.85,-9), Enum.Material.Leather, Color3.fromRGB(180,150,110), Enum.PartType.Ball)
local i=16
for _, z in ipairs({2,7,12,17}) do
  i=i+1
  P('CastleStallBunting'..i, Vector3.new(1.1,1.1,0.3), CFrame.new(12.8,4.6,z), Enum.Material.Fabric, Color3.fromRGB(i*7-100,80+i*3,160), B)
end
report('campfire, long market awning, sacks, bunting')
end)

run_chunk(15, function()
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block
-- garden beds (low) west courtyard
P('CastleGardenFrame1', Vector3.new(9,1.1,0.6), CFrame.new(-19,0.55,14), Enum.Material.Wood, Color3.fromRGB(226,222,212), B)
P('CastleGardenFrame2', Vector3.new(9,1,0.6), CFrame.new(-19,0.5,20), Enum.Material.Wood, Color3.fromRGB(222,218,208), B)
P('CastleGardenFrame3', Vector3.new(0.6,0.9,6.4), CFrame.new(-23.4,0.45,17), Enum.Material.Wood, Color3.fromRGB(224,220,210), B)
P('CastleGardenSoil', Vector3.new(8.8,1,5.8), CFrame.new(-19,0.5,17), Enum.Material.Ground, Color3.fromRGB(120,100,80), B)
P('CastleGardenCrop1', Vector3.new(2.6,1.4,2.6), CFrame.new(-21,1.6,16), Enum.Material.LeafyGrass, Color3.fromRGB(120,150,90), B)
P('CastleGardenCrop2', Vector3.new(2.2,1.6,2.2), CFrame.new(-17.4,1.7,18.2), Enum.Material.Grass, Color3.fromRGB(130,160,95), B)
-- hay cart
P('CastleCartBed', Vector3.new(5,2.2,3.2), CFrame.new(-7,1.6,24), Enum.Material.WoodPlanks, Color3.fromRGB(230,226,214), B)
P('CastleCartWheelW', Vector3.new(0.5,3,3), CFrame.new(-7,1.5,22.1), Enum.Material.Wood, Color3.fromRGB(220,214,204), B)
P('CastleCartWheelE', Vector3.new(0.5,2.8,2.8), CFrame.new(-7,1.5,25.9), Enum.Material.Wood, Color3.fromRGB(216,210,200), B)
P('CastleCartHay', Vector3.new(4.4,1.6,2.8), CFrame.new(-7,3.4,24), Enum.Material.Fabric, Color3.fromRGB(228,208,130), B)
P('CastleCartHay2', Vector3.new(2,1.6,2), CFrame.new(-8.2,4.6,24.2)*CFrame.Angles(0,math.rad(16),0), Enum.Material.Fabric, Color3.fromRGB(220,200,124), B)
-- woodpile behind keep
P('CastleWoodLog1', Vector3.new(6,0.9,0.9), CFrame.new(9,0.45,21), Enum.Material.Wood, Color3.fromRGB(224,218,206), Enum.PartType.Cylinder)
P('CastleWoodLog2', Vector3.new(6,0.9,0.9), CFrame.new(9,1.35,21.1), Enum.Material.Wood, Color3.fromRGB(220,214,202), Enum.PartType.Cylinder)
P('CastleWoodLog3', Vector3.new(5.6,0.85,0.85), CFrame.new(9,2.2,21.05), Enum.Material.Wood, Color3.fromRGB(226,220,208), Enum.PartType.Cylinder)
P('CastleWoodLog4', Vector3.new(5.8,0.8,0.8), CFrame.new(9,3.0,20.95), Enum.Material.Wood, Color3.fromRGB(218,212,200), Enum.PartType.Cylinder)
-- barrels near keep and gate
P('CastleBarrel6', Vector3.new(1.5,2.2,1.5), CFrame.new(12.5,1.1,-43), Enum.Material.Wood, Color3.fromRGB(228,224,212), Enum.PartType.Cylinder)
P('CastleBarrel7', Vector3.new(1.4,2,1.4), CFrame.new(13.6,1,-41.2), Enum.Material.Wood, Color3.fromRGB(224,220,208), Enum.PartType.Cylinder)
P('CastleCrate6', Vector3.new(1.6,1.6,1.6), CFrame.new(11.4,0.8,-45), Enum.Material.WoodPlanks, Color3.fromRGB(230,226,216), B)
-- benches in courtyard
P('CastleBenchSeat3', Vector3.new(1.4,0.4,5.2), CFrame.new(-11,1.35,-30), Enum.Material.WoodPlanks, Color3.fromRGB(228,224,212), B)
P('CastleBenchLeg1', Vector3.new(1.2,1.2,0.6), CFrame.new(-11,0.6,-32), Enum.Material.Wood, Color3.fromRGB(222,218,206), B)
P('CastleBenchLeg2', Vector3.new(1.2,1.2,0.55), CFrame.new(-11,0.6,-28), Enum.Material.Wood, Color3.fromRGB(218,214,202), B)
report('gardens, hay cart, woodpile, barrels, bench')
end)

run_chunk(16, function()
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block
-- well at (14, 22)
P('CastleWellRing1', Vector3.new(3.2,1.6,0.7), CFrame.new(14,0.8,19.6), Enum.Material.Cobblestone, Color3.fromRGB(226,224,216), B)
P('CastleWellRing2', Vector3.new(3,1.5,0.7), CFrame.new(14,0.75,24.4), Enum.Material.Cobblestone, Color3.fromRGB(222,220,212), B)
P('CastleWellRing3', Vector3.new(0.7,1.55,3.6), CFrame.new(11.7,0.78,22), Enum.Material.Cobblestone, Color3.fromRGB(224,222,214), B)
P('CastleWellRing4', Vector3.new(0.7,1.6,3.6), CFrame.new(16.3,0.8,22), Enum.Material.Cobblestone, Color3.fromRGB(220,218,210), B)
P('CastleWellWater', Vector3.new(2.2,1.2,2.2), CFrame.new(14,0.3,22), Enum.Material.Glass, Color3.fromRGB(90,120,150), B)
P('CastleWellPost1', Vector3.new(0.45,4,0.45), CFrame.new(12.2,3.8,22), Enum.Material.Wood, Color3.fromRGB(226,222,212), B)
P('CastleWellPost2', Vector3.new(0.45,3.8,0.45), CFrame.new(15.8,3.7,22), Enum.Material.Wood, Color3.fromRGB(222,218,208), B)
P('CastleWellBeam', Vector3.new(4.6,0.45,0.45), CFrame.new(14,5.7,22), Enum.Material.WoodPlanks, Color3.fromRGB(230,226,216), B)
P('CastleWellRoofW', Vector3.new(2.9,0.35,3.4), CFrame.new(12.7,6.5,22)*CFrame.Angles(0,0,math.rad(-30)), Enum.Material.RoofShingles, Color3.fromRGB(214,208,196), B)
P('CastleWellRoofE', Vector3.new(2.9,0.35,3.4), CFrame.new(15.3,6.5,22)*CFrame.Angles(0,0,math.rad(30)), Enum.Material.RoofShingles, Color3.fromRGB(210,204,192), B)
P('CastleWellBucket', Vector3.new(0.9,0.9,0.9), CFrame.new(14,4.4,22), Enum.Material.WoodPlanks, Color3.fromRGB(224,220,208), Enum.PartType.Cylinder)
-- stairs up to north wall walk
P('CastleWallStep1', Vector3.new(4,0.6,1.4), CFrame.new(-8,0.3,-24.6), Enum.Material.Pavement, Color3.fromRGB(224,222,214), Enum.PartType.Wedge)
P('CastleWallStep2', Vector3.new(4,1.4,1.4), CFrame.new(-8,0.7,-25.9), Enum.Material.Pavement, Color3.fromRGB(220,218,210), Enum.PartType.Wedge)
P('CastleWallStep3', Vector3.new(4,2.2,1.5), CFrame.new(-8,1.1,-27.2), Enum.Material.Pavement, Color3.fromRGB(216,214,206), Enum.PartType.Wedge)
-- gatehouse torches (flanking the arch, outside)
P('CastleGateTorchW', Vector3.new(0.7,1.4,0.7), CFrame.new(-4.4,10,-35.3), Enum.Material.Neon, Color3.fromRGB(255,150,60), B)
P('CastleGateTorchE', Vector3.new(0.7,1.4,0.7), CFrame.new(4.4,10,-35.3), Enum.Material.Neon, Color3.fromRGB(255,150,60), B)
P('CastleGateBracketW', Vector3.new(0.9,0.4,1.2), CFrame.new(-4.4,9.2,-35.6), Enum.Material.CorrodedMetal, Color3.fromRGB(230,228,220), B)
P('CastleGateBracketE', Vector3.new(0.9,0.4,1.2), CFrame.new(4.4,9.2,-35.6), Enum.Material.CorrodedMetal, Color3.fromRGB(230,228,220), B)
report('well, wall steps, gate torches')
end)

run_chunk(17, function()
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block
local CW=Enum.PartType.CornerWedge
-- east outside camp tents
P('CastleTentAx', Vector3.new(7,7,7), CFrame.new(44,3.5,-14), Enum.Material.Fabric, Color3.fromRGB(120,110,150), CW)
P('CastleTentBx', Vector3.new(7,7,7), CFrame.new(44,3.5,-20)*CFrame.Angles(0,math.rad(180),0), Enum.Material.Fabric, Color3.fromRGB(130,118,158), CW)
P('CastleTentCx', Vector3.new(6.6,6.6,6.6), CFrame.new(43.6,3.3,-22.4)*CFrame.Angles(0,math.rad(-90),0), Enum.Material.Fabric, Color3.fromRGB(112,102,142), CW)
P('CastleTentPoleE', Vector3.new(0.4,7.6,0.4), CFrame.new(44,3.8,-17), Enum.Material.Wood, Color3.fromRGB(226,222,212), B)
P('CastleTentPoleF', Vector3.new(0.4,7.2,0.4), CFrame.new(48,3.6,-13), Enum.Material.Wood, Color3.fromRGB(222,218,208), B)
P('CastleTentTorchA', Vector3.new(0.8,8,0.8), CFrame.new(40,4,-17), Enum.Material.WoodPlanks, Color3.fromRGB(228,224,214), Enum.PartType.Cylinder)
P('CastleTentTorchFlame', Vector3.new(1.2,1.6,1.2), CFrame.new(40,8.4,-17)*CFrame.Angles(math.rad(8),math.rad(20),0), Enum.Material.Neon, Color3.fromRGB(255,170,60), Enum.PartType.Wedge)
-- rocks to the east
P('CastleRockA', Vector3.new(6.5,6.5,6.5), CFrame.new(50,2.4,-6)*CFrame.Angles(0.2,0.5,0.15), Enum.Material.Rock, Color3.fromRGB(224,222,214), Enum.PartType.Ball)
P('CastleRockB', Vector3.new(5,5,5), CFrame.new(53,1.9,-11)*CFrame.Angles(0.4,1.1,0.25), Enum.Material.Slate, Color3.fromRGB(218,216,208), Enum.PartType.Ball)
P('CastleRockC', Vector3.new(4.4,4.4,4.4), CFrame.new(47,1.6,-1)*CFrame.Angles(0.3,0.2,0.5), Enum.Material.Basalt, Color3.fromRGB(210,208,202), Enum.PartType.Ball)
P('CastleRockD', Vector3.new(3.6,3.6,3.6), CFrame.new(52,1.3,-2)*CFrame.Angles(0.7,0.9,0.2), Enum.Material.Granite, Color3.fromRGB(222,220,212), Enum.PartType.Ball)
-- stone fence west of bridge
local i=0
for _, z in ipairs({-4,3,10}) do
  i=i+1
  P('CastleFencePostA'..i, Vector3.new(1.2,2.6,1.2), CFrame.new(-9,1.3,z), Enum.Material.Granite, Color3.fromRGB(226,224,216), B)
end
P('CastleFenceRailA1', Vector3.new(0.6,0.5,15), CFrame.new(-9,1.9,3), Enum.Material.WoodPlanks, Color3.fromRGB(228,224,214), B)
P('CastleFenceRailA2', Vector3.new(0.6,0.5,14.6), CFrame.new(-9,1.0,3.2), Enum.Material.WoodPlanks, Color3.fromRGB(224,220,210), B)
for _, z in ipairs({-3,4,11}) do
  i=i+1
  P('CastleFencePostA'..i, Vector3.new(1.1,2.4,1.1), CFrame.new(9,1.2,z), Enum.Material.Granite, Color3.fromRGB(222,220,212), B)
end
P('CastleFenceRailB1', Vector3.new(0.6,0.45,15.2), CFrame.new(9,1.8,4), Enum.Material.WoodPlanks, Color3.fromRGB(226,222,212), B)
report('tents, rocks, roadside fences')
end)

run_chunk(18, function()
local parts = {}
for _,d in ipairs(workspace:GetDescendants()) do
  if d:IsA('Part') and d.Name:sub(1,6)=='Castle' then table.insert(parts,d) end
end
local n=#parts
local mats, shapes = {}, {}
for _,p in ipairs(parts) do mats[p.Material.Name]=true shapes[p.Shape.Name]=true end
local mc=0 for _ in pairs(mats) do mc=mc+1 end
local sc=0 local sks={} for k in pairs(shapes) do sc=sc+1 table.insert(sks,k) end
-- bounds
local minx,maxx,miny,maxy,minz,maxz = 1e9,-1e9,1e9,-1e9,1e9,-1e9
for _,p in ipairs(parts) do
  local sz=p.Size
  minx=math.min(minx,p.Position.X-sz.X/2) maxx=math.max(maxx,p.Position.X+sz.X/2)
  miny=math.min(miny,p.Position.Y-sz.Y/2) maxy=math.max(maxy,p.Position.Y+sz.Y/2)
  minz=math.min(minz,p.Position.Z-sz.Z/2) maxz=math.max(maxz,p.Position.Z+sz.Z/2)
end
-- floating check
local function box(p) local s=p.Size return {x0=p.Position.X-s.X/2,x1=p.Position.X+s.X/2,y0=p.Position.Y-s.Y/2,y1=p.Position.Y+s.Y/2,z0=p.Position.Z-s.Z/2,z1=p.Position.Z+s.Z/2} end
local bb={} for i,p in ipairs(parts) do bb[i]=box(p) end
local floating, inside, thin, squashed = 0,0,0,0
for i,p in ipairs(parts) do
  local a=bb[i]
  if (p.Size.X<0.05) or (p.Size.Y<0.05) or (p.Size.Z<0.05) then thin=thin+1 end
  if (p.Shape==Enum.PartType.Cylinder or p.Shape==Enum.PartType.Ball or p.Shape==Enum.PartType.Rounded) and math.abs(p.Size.Y-p.Size.Z)>0.3 then squashed=squashed+1 end
  local sup=false
  if a.y0 < 0.6 then sup=true end
  if not sup then
    for j,q in ipairs(parts) do
      if j~=i then local b=bb[j]
        local ox = math.min(a.x1,b.x1)-math.max(a.x0,b.x0)
        local oz = math.min(a.z1,b.z1)-math.max(a.z0,b.z0)
        if ox > -0.5 and oz > -0.5 and math.abs(a.y0-b.y1) < 0.5 then sup=true break end
      end
    end
  end
  if not sup then floating=floating+1 end
  -- fully inside another block
  for j,q in ipairs(parts) do
    if j~=i and q.Shape==Enum.PartType.Block then local b=bb[j]
      if a.x0>b.x0+0.1 and a.x1<b.x1-0.1 and a.y0>b.y0+0.1 and a.y1<b.y1-0.1 and a.z0>b.z0+0.1 and a.z1<b.z1-0.1 then inside=inside+1 break end
    end
  end
end
-- duplicate shape/size/pos
local seen, dup = {}, 0
for _,p in ipairs(parts) do
  local k=p.Shape.Name..'|'..math.floor(p.Size.X*10)..','..math.floor(p.Size.Y*10)..','..math.floor(p.Size.Z*10)..'|'..math.floor(p.Position.X*10)..','..math.floor(p.Position.Y*10)..','..math.floor(p.Position.Z*10)
  if seen[k] then dup=dup+1 else seen[k]=true end
end
return string.format("parts=%d mats=%d shapes=%d [%s] bounds x %.1f..%.1f y %.1f..%.1f z %.1f..%.1f floating=%d inside=%d thin=%d squashed=%d dup=%d", n, mc, sc, table.concat(sks,','), minx,maxx,miny,maxy,minz,maxz, floating, inside, thin, squashed, dup)
end)

run_chunk(19, function()
local parts = {}
for _,d in ipairs(workspace:GetDescendants()) do
  if d:IsA('Part') and d.Name:sub(1,6)=='Castle' then table.insert(parts,d) end
end
local n=#parts
local mats, shapes = {}, {}
for _,p in ipairs(parts) do mats[p.Material.Name]=true shapes[p.Shape.Name]=true end
local mc=0 for _ in pairs(mats) do mc=mc+1 end
local sc=0 local sks={} for k in pairs(shapes) do sc=sc+1 table.insert(sks,k) end
local minx,maxx,miny,maxy,minz,maxz = 1e9,-1e9,1e9,-1e9,1e9,-1e9
for _,p in ipairs(parts) do
  local sz=p.Size
  minx=math.min(minx,p.Position.X-sz.X/2) maxx=math.max(maxx,p.Position.X+sz.X/2)
  miny=math.min(miny,p.Position.Y-sz.Y/2) maxy=math.max(maxy,p.Position.Y+sz.Y/2)
  minz=math.min(minz,p.Position.Z-sz.Z/2) maxz=math.max(maxz,p.Position.Z+sz.Z/2)
end
local function box(p) local s=p.Size return {x0=p.Position.X-s.X/2,x1=p.Position.X+s.X/2,y0=p.Position.Y-s.Y/2,y1=p.Position.Y+s.Y/2,z0=p.Position.Z-s.Z/2,z1=p.Position.Z+s.Z/2} end
local bb={} for i,p in ipairs(parts) do bb[i]=box(p) end
local floating, inside, thin, squashed = 0,0,0,0
local fl = {}
for i,p in ipairs(parts) do
  local a=bb[i]
  if (p.Size.X<0.05) or (p.Size.Y<0.05) or (p.Size.Z<0.05) then thin=thin+1 end
  if (p.Shape==Enum.PartType.Cylinder or p.Shape==Enum.PartType.Ball) and math.abs(p.Size.Y-p.Size.Z)>0.3 then squashed=squashed+1 end
  local sup=false
  if a.y0 < 0.6 then sup=true end
  if not sup then
    for j,q in ipairs(parts) do
      if j~=i then local b=bb[j]
        local ox = math.min(a.x1,b.x1)-math.max(a.x0,b.x0)
        local oz = math.min(a.z1,b.z1)-math.max(a.z0,b.z0)
        local oy = math.min(a.y1,b.y1)-math.max(a.y0,b.y0)
        if ox > -0.5 and oz > -0.5 and oy > -0.5 then sup=true break end
      end
    end
  end
  if not sup then floating=floating+1 if #fl<12 then table.insert(fl, p.Name) end end
  for j,q in ipairs(parts) do
    if j~=i and q.Shape==Enum.PartType.Block then local b=bb[j]
      if a.x0>b.x0+0.1 and a.x1<b.x1-0.1 and a.y0>b.y0+0.1 and a.y1<b.y1-0.1 and a.z0>b.z0+0.1 and a.z1<b.z1-0.1 then inside=inside+1 break end
    end
  end
end
local seen, dup = {}, 0
for _,p in ipairs(parts) do
  local k=p.Shape.Name..'|'..math.floor(p.Size.X*10)..','..math.floor(p.Size.Y*10)..','..math.floor(p.Size.Z*10)..'|'..math.floor(p.Position.X*10)..','..math.floor(p.Position.Y*10)..','..math.floor(p.Position.Z*10)
  if seen[k] then dup=dup+1 else seen[k]=true end
end
-- materials list
local ml={} for k in pairs(mats) do table.insert(ml,k) end table.sort(ml)
return string.format("parts=%d mats=%d [%s] shapes=%d [%s] bounds x %.1f..%.1f y %.1f..%.1f z %.1f..%.1f floating=%d inside=%d thin=%d squashed=%d dup=%d | fl: %s", n, mc, table.concat(ml,','), sc, table.concat(sks,','), minx,maxx,miny,maxy,minz,maxz, floating, inside, thin, squashed, dup, table.concat(fl,','))
end)

run_chunk(20, function()
local function kill(n) local o = workspace:FindFirstChild(n) if o then o:Destroy() end end
-- remove stray probes and mis-shapen barrels/torch
for _,d in ipairs(workspace:GetChildren()) do if d:IsA('Part') and d.Name:sub(1,13)=='CastleProbeT' then d:Destroy() end end
for _, n in ipairs({'CastleBarrel6','CastleBarrel7','CastleTentTorchA','CastleWellBucket','CastleStallBunting17','CastleStallBunting18','CastleStallBunting19','CastleStallBunting20'}) do kill(n) end
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block
local CY=Enum.PartType.Cylinder
P('CastleBarrel6', Vector3.new(2.2,1.5,1.5), CFrame.new(12.5,1.1,-43)*CFrame.Angles(0,0,math.rad(90)), Enum.Material.Wood, Color3.fromRGB(228,224,212), CY)
P('CastleBarrel7', Vector3.new(2,1.4,1.4), CFrame.new(13.6,1.1,-41.2)*CFrame.Angles(0,0,math.rad(90)), Enum.Material.Wood, Color3.fromRGB(224,220,208), CY)
P('CastleTentTorchA', Vector3.new(8,0.8,0.8), CFrame.new(40,4,-17)*CFrame.Angles(0,0,math.rad(90)), Enum.Material.WoodPlanks, Color3.fromRGB(228,224,214), CY)
P('CastleWellRope', Vector3.new(1.1,0.22,0.22), CFrame.new(14,5.0,22)*CFrame.Angles(0,0,math.rad(90)), Enum.Material.Fabric, Color3.fromRGB(210,200,180), CY)
P('CastleWellBucket', Vector3.new(1.1,0.95,0.95), CFrame.new(14,4.0,22)*CFrame.Angles(0,0,math.rad(90)), Enum.Material.WoodPlanks, Color3.fromRGB(224,220,208), CY)
-- bunting hung from awning edge
local i=16
for _, z in ipairs({2,7,12,17}) do
  i=i+1
  P('CastleStallBunting'..i, Vector3.new(1.1,1.2,0.3), CFrame.new(16.2,6.1,z), Enum.Material.Fabric, Color3.fromRGB(i*7-100,80+i*3,160), B)
end
report('fixed shapes and floating props')
end)

run_chunk(21, function()
local parts = {}
for _,d in ipairs(workspace:GetDescendants()) do if d:IsA('Part') and d.Name:sub(1,6)=='Castle' then table.insert(parts,d) end end
local function box(p) local s=p.Size return {p.Position.X-s.X/2,p.Position.X+s.X/2,p.Position.Y-s.Y/2,p.Position.Y+s.Y/2,p.Position.Z-s.Z/2,p.Position.Z+s.Z/2} end
local bb={} for i,p in ipairs(parts) do bb[i]=box(p) end
local bad = 0 local names = {}
for i,p in ipairs(parts) do
  local a=bb[i]; local flag=false
  for j,q in ipairs(parts) do
    if j~=i and q.Shape==Enum.PartType.Block and not flag then
      local b=bb[j]
      local ov = math.min(a[2],b[2])-math.max(a[1],b[1]) > 0.1 and math.min(a[4],b[4])-math.max(a[3],b[3]) > 0.1 and math.min(a[6],b[6])-math.max(a[5],b[5]) > 0.1
      if ov then
        local planes = ((math.abs(a[1]-b[1])<0.06 or math.abs(a[2]-b[2])<0.06) and (math.min(a[6],b[6])-math.max(a[5],b[5])>0.1 and math.min(a[4],b[4])-math.max(a[3],b[3])>0.1))
          or ((math.abs(a[3]-b[3])<0.06 or math.abs(a[4]-b[4])<0.06) and (math.min(a[2],b[2])-math.max(a[1],b[1])>0.1 and math.min(a[6],b[6])-math.max(a[5],b[5])>0.1))
          or ((math.abs(a[5]-b[5])<0.06 or math.abs(a[6]-b[6])<0.06) and (math.min(a[2],b[2])-math.max(a[1],b[1])>0.1 and math.min(a[4],b[4])-math.max(a[3],b[3])>0.1))
        if planes then flag=true end
      end
    end
  end
  if flag then bad=bad+1 if #names<15 then table.insert(names,p.Name) end end
end
return string.format("total=%d flushBad=%d pct=%.1f%% :: %s", #parts, bad, bad/#parts*100, table.concat(names,','))
end)

run_chunk(22, function()
local parts = {}
for _,d in ipairs(workspace:GetDescendants()) do if d:IsA('Part') and d.Name:sub(1,6)=='Castle' then table.insert(parts,d) end end
local function box(p) local s=p.Size return {p.Position.X-s.X/2,p.Position.X+s.X/2,p.Position.Y-s.Y/2,p.Position.Y+s.Y/2,p.Position.Z-s.Z/2,p.Position.Z+s.Z/2} end
local bb={} for i,p in ipairs(parts) do bb[i]=box(p) end
local function ov1(a,b,i,j) return math.min(a[j],b[j])-math.max(a[i],b[i]) end
local bad = 0 local names = {}
for i,p in ipairs(parts) do
  local a=bb[i]; local flag=false
  for j,q in ipairs(parts) do
    if j~=i and q.Shape==Enum.PartType.Block and not flag then
      local b=bb[j]
      -- X axis
      if (math.abs(a[1]-b[1])<0.06 or math.abs(a[2]-b[2])<0.06) and ov1(a,b,3,4)>0.1 and ov1(a,b,5,6)>0.1 then flag=true end
      if (math.abs(a[3]-b[3])<0.06 or math.abs(a[4]-b[4])<0.06) and ov1(a,b,1,2)>0.1 and ov1(a,b,5,6)>0.1 then flag=true end
      if (math.abs(a[5]-b[5])<0.06 or math.abs(a[6]-b[6])<0.06) and ov1(a,b,1,2)>0.1 and ov1(a,b,3,4)>0.1 then flag=true end
    end
  end
  if flag then bad=bad+1 if #names<20 then table.insert(names,p.Name) end end
end
return string.format("total=%d flushBad=%d pct=%.1f%% :: %s", #parts, bad, bad/#parts*100, table.concat(names,','))
end)

run_chunk(23, function()
local parts = {}
for _,d in ipairs(workspace:GetDescendants()) do if d:IsA('Part') and d.Name:sub(1,6)=='Castle' then table.insert(parts,d) end end
local function box(p) local s=p.Size return {p.Position.X-s.X/2,p.Position.X+s.X/2,p.Position.Y-s.Y/2,p.Position.Y+s.Y/2,p.Position.Z-s.Z/2,p.Position.Z+s.Z/2} end
local function ov1(a,b,i,j) return math.min(a[j],b[j])-math.max(a[i],b[i]) end
local function flagsOn(a,b)
  local r={}
  if (math.abs(a[1]-b[1])<0.06 or math.abs(a[2]-b[2])<0.06) and ov1(a,b,3,4)>0.1 and ov1(a,b,5,6)>0.1 then r.X=true end
  if (math.abs(a[3]-b[3])<0.06 or math.abs(a[4]-b[4])<0.06) and ov1(a,b,1,2)>0.1 and ov1(a,b,5,6)>0.1 then r.Y=true end
  if (math.abs(a[5]-b[5])<0.06 or math.abs(a[6]-b[6])<0.06) and ov1(a,b,1,2)>0.1 and ov1(a,b,3,4)>0.1 then r.Z=true end
  return r
end
local fixed = 0
for i = #parts, 1, -1 do
  local p = parts[i]
  for pass = 1, 6 do
    local a = box(p); local axis = nil
    for j = 1, #parts do
      local q = parts[j]
      if j ~= i and q.Shape == Enum.PartType.Block then
        local r = flagsOn(a, box(q))
        if r.X then axis='X' elseif r.Y then axis='Y' elseif r.Z then axis='Z' end
        if axis then break end
      end
    end
    if not axis then break end
    local s = p.Size
    if axis=='X' then p.Size = Vector3.new(math.max(0.3, s.X-0.35), s.Y, s.Z)
    elseif axis=='Y' then p.Size = Vector3.new(s.X, math.max(0.3, s.Y-0.35), s.Z)
    else p.Size = Vector3.new(s.X, s.Y, math.max(0.3, s.Z-0.35)) end
    fixed = fixed + 1
  end
end
-- recount
local parts2 = {}
for _,d in ipairs(workspace:GetDescendants()) do if d:IsA('Part') and d.Name:sub(1,6)=='Castle' then table.insert(parts2,d) end end
local bb={} for i,p in ipairs(parts2) do bb[i]=box(p) end
local bad=0
for i,p in ipairs(parts2) do
  local a=bb[i]
  for j,q in ipairs(parts2) do
    if j~=i and q.Shape==Enum.PartType.Block then
      local r = flagsOn(a, bb[j])
      if r.X or r.Y or r.Z then bad=bad+1 break end
    end
  end
end
return string.format("edits=%d total=%d flushBad=%d pct=%.1f%%", fixed, #parts2, bad, bad/#parts2*100)
end)

run_chunk(24, function()
local parts = {}
for _,d in ipairs(workspace:GetDescendants()) do if d:IsA('Part') and d.Name:sub(1,6)=='Castle' then table.insert(parts,d) end end
local function box(p) local s=p.Size return {p.Position.X-s.X/2,p.Position.X+s.X/2,p.Position.Y-s.Y/2,p.Position.Y+s.Y/2,p.Position.Z-s.Z/2,p.Position.Z+s.Z/2} end
local bb={} for i,p in ipairs(parts) do bb[i]=box(p) end
local fl={}
for i,p in ipairs(parts) do
  local a=bb[i]; local sup=false
  if a[3] < 0.6 then sup=true end
  if not sup then
    for j,q in ipairs(parts) do
      if j~=i then local b=bb[j]
        local ox=(math.min(a[2],b[2])-math.max(a[1],b[1])) local oz=(math.min(a[6],b[6])-math.max(a[5],b[5])) local oy=(math.min(a[4],b[4])-math.max(a[3],b[3]))
        if ox>0.45 and oz>0.45 and oy>0.45 then sup=true break end
      end
    end
  end
  if not sup then table.insert(fl, p.Name..'@'..math.floor(p.Position.Y*10)/10) end
end
return string.format("total=%d floating=%d :: %s", #parts, #fl, table.concat(fl,','))
end)

run_chunk(25, function()
local parts = {}
for _,d in ipairs(workspace:GetDescendants()) do if d:IsA('Part') and d.Name:sub(1,6)=='Castle' then table.insert(parts,d) end end
local function box(p) local s=p.Size return {p.Position.X-s.X/2,p.Position.X+s.X/2,p.Position.Y-s.Y/2,p.Position.Y+s.Y/2,p.Position.Z-s.Z/2,p.Position.Z+s.Z/2} end
local bb={} for i,p in ipairs(parts) do bb[i]=box(p) end
local fl={}
for i,p in ipairs(parts) do
  local a=bb[i]; local sup=false
  if a[3] < 0.6 then sup=true end
  if not sup then
    for j,q in ipairs(parts) do
      if j~=i then local b=bb[j]
        local ox=(math.min(a[2],b[2])-math.max(a[1],b[1])) local oz=(math.min(a[6],b[6])-math.max(a[5],b[5]))
        local dy = math.abs(a[3]-b[4])
        if ox > -0.3 and oz > -0.3 and dy < 0.5 then sup=true break end
      end
    end
  end
  if not sup then table.insert(fl, p.Name..'@'..math.floor(p.Position.Y*10)/10) end
end
return string.format("total=%d floating=%d :: %s", #parts, #fl, table.concat(fl,','))
end)

run_chunk(26, function()
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local function M(n,s,c,m,col,sh)
  local o = workspace:FindFirstChild(n)
  if o then o.Size=s o.CFrame=c o.Material=m o.Color=col or Color3.fromRGB(230,228,220) if sh then o.Shape=sh end return o end
  return P(n,s,c,m,col,sh)
end
local B=Enum.PartType.Block
M('CastleGateBracketW', Vector3.new(0.9,0.45,1.3), CFrame.new(-4.4,9.9,-35.5), Enum.Material.CorrodedMetal, Color3.fromRGB(232,230,222), B)
M('CastleGateBracketE', Vector3.new(0.9,0.45,1.3), CFrame.new(4.4,9.9,-35.5), Enum.Material.CorrodedMetal, Color3.fromRGB(232,230,222), B)
M('CastleGateTorchW', Vector3.new(0.75,1.5,0.75), CFrame.new(-4.4,10.9,-35.4), Enum.Material.Neon, Color3.fromRGB(255,150,60), B)
M('CastleGateTorchE', Vector3.new(0.75,1.5,0.75), CFrame.new(4.4,10.9,-35.4), Enum.Material.Neon, Color3.fromRGB(255,150,60), B)
M('CastleTentTorchFlame', Vector3.new(1.3,1.7,1.3), CFrame.new(40,8.6,-17)*CFrame.Angles(0,math.rad(25),0), Enum.Material.Neon, Color3.fromRGB(255,170,60), Enum.PartType.Wedge)
M('CastleStallBunting17', Vector3.new(1.2,1.3,0.3), CFrame.new(16.05,7.0,2), Enum.Material.Fabric, Color3.fromRGB(90,110,170), B)
M('CastleStallBunting18', Vector3.new(1.2,1.3,0.3), CFrame.new(16.05,7.0,7), Enum.Material.Fabric, Color3.fromRGB(140,80,150), B)
M('CastleStallBunting19', Vector3.new(1.2,1.3,0.3), CFrame.new(16.05,7.0,12), Enum.Material.Fabric, Color3.fromRGB(180,90,90), B)
M('CastleStallBunting20', Vector3.new(1.2,1.3,0.3), CFrame.new(16.05,7.0,17), Enum.Material.Fabric, Color3.fromRGB(90,150,140), B)
report('torch brackets and stall flags fixed')
end)

run_chunk(27, function()
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local function M(n,s,c,m,col,sh)
  local o = workspace:FindFirstChild(n)
  if o then o.Size=s o.CFrame=c o.Material=m o.Color=col or Color3.fromRGB(230,228,220) if sh then o.Shape=sh end return o end
  return P(n,s,c,m,col,sh)
end
local B=Enum.PartType.Block
local CY=Enum.PartType.Cylinder
-- bunting posts so the flags have something to hang from
P('CastleStallPostA', Vector3.new(0.5,7.6,0.5), CFrame.new(16.1,3.8,1), Enum.Material.WoodPlanks, Color3.fromRGB(228,224,214), B)
P('CastleStallPostB', Vector3.new(0.5,7.6,0.5), CFrame.new(16.1,3.8,18), Enum.Material.WoodPlanks, Color3.fromRGB(224,220,210), B)
P('CastleStallRope', Vector3.new(17.2,0.18,0.18), CFrame.new(16.1,7.3,9.5)*CFrame.Angles(0,0,math.rad(90)), Enum.Material.Fabric, Color3.fromRGB(200,190,170), CY)
M('CastleStallBunting17', Vector3.new(1.2,1.5,0.3), CFrame.new(16.1,6.5,2.4), Enum.Material.Fabric, Color3.fromRGB(90,110,170), B)
M('CastleStallBunting18', Vector3.new(1.2,1.5,0.3), CFrame.new(16.1,6.5,7.0), Enum.Material.Fabric, Color3.fromRGB(140,80,150), B)
M('CastleStallBunting19', Vector3.new(1.2,1.5,0.3), CFrame.new(16.1,6.5,11.6), Enum.Material.Fabric, Color3.fromRGB(180,90,90), B)
M('CastleStallBunting20', Vector3.new(1.2,1.5,0.3), CFrame.new(16.1,6.5,16.2), Enum.Material.Fabric, Color3.fromRGB(90,150,140), B)
-- well bucket set down on the rim instead of hanging
M('CastleWellBucket', Vector3.new(1,1.3,1.3), CFrame.new(14.6,3.4,21.6)*CFrame.Angles(0,math.rad(15),math.rad(90)), Enum.Material.WoodPlanks, Color3.fromRGB(224,220,208), CY)
local o = workspace:FindFirstChild('CastleWellRope') if o then o:Destroy() end
-- a couple of pigeons/crates on the wall walk for life
P('CastleWallCrateN', Vector3.new(1.8,1.8,1.8), CFrame.new(-12,5.9,27.6)*CFrame.Angles(0,math.rad(12),0), Enum.Material.WoodPlanks, Color3.fromRGB(226,222,212), B)
P('CastleWallCrateE', Vector3.new(1.6,1.6,1.6), CFrame.new(27.6,5.8,-16)*CFrame.Angles(0,math.rad(-20),0), Enum.Material.Wood, Color3.fromRGB(224,220,210), B)
report('stall posts, flags, bucket, wall crates')
end)

run_chunk(28, function()
local parts = {}
for _,d in ipairs(workspace:GetDescendants()) do if d:IsA('Part') and d.Name:sub(1,6)=='Castle' then table.insert(parts,d) end end
local function box(p) local s=p.Size return {p.Position.X-s.X/2,p.Position.X+s.X/2,p.Position.Y-s.Y/2,p.Position.Y+s.Y/2,p.Position.Z-s.Z/2,p.Position.Z+s.Z/2} end
local bb={} for i,p in ipairs(parts) do bb[i]=box(p) end
local fl={}
local function ov1(a,b,i,j) return math.min(a[j],b[j])-math.max(a[i],b[i]) end
local function flush(a,b)
  if (math.abs(a[1]-b[1])<0.06 or math.abs(a[2]-b[2])<0.06) and ov1(a,b,3,4)>0.1 and ov1(a,b,5,6)>0.1 then return true end
  if (math.abs(a[3]-b[3])<0.06 or math.abs(a[4]-b[4])<0.06) and ov1(a,b,1,2)>0.1 and ov1(a,b,5,6)>0.1 then return true end
  if (math.abs(a[5]-b[5])<0.06 or math.abs(a[6]-b[6])<0.06) and ov1(a,b,1,2)>0.1 and ov1(a,b,3,4)>0.1 then return true end
  return false
end
local fal=0
for i,p in ipairs(parts) do
  local a=bb[i]; local sup=(a[3]<0.6)
  if not sup then
    for j,q in ipairs(parts) do if j~=i then local b=bb[j]
      if ov1(a,b,1,2)>-0.3 and ov1(a,b,5,6)>-0.3 and math.abs(a[3]-b[4])<0.5 then sup=true break end
    end end
  end
  if not sup then table.insert(fl,p.Name) end
  local f=false
  for j,q in ipairs(parts) do if j~=i and q.Shape==Enum.PartType.Block then if flush(a,bb[j]) then f=true break end end end
  if f then fal=fal+1 end
end
-- gate gap: find tallest obstacle in the gate column x -4..4, z -24..-8
local minTop=0
for _,p in ipairs(parts) do
  local a=box(p)
  if math.abs(p.Position.X)<4.2 and p.Position.Z>-24 and p.Position.Z<-8 and p.Size.Y>1 then
    if a[3]<3.5 and a[4]<5 and a[3]>1 then if minTop==0 or a[3]<minTop then minTop=a[3] end end
  end
end
local mats={} local shapes={}
for _,p in ipairs(parts) do mats[p.Material.Name]=true shapes[p.Shape.Name]=true end
local mc=0 for _ in pairs(mats) do mc=mc+1 end
local sc=0 for _ in pairs(shapes) do sc=sc+1 end
return string.format("total=%d floating=%d flush=%d(%.1f%%) mats=%d shapes=%d gateObstacleBottom=%s :: %s", #parts, #fl, fal, fal/#parts*100, mc, sc, tostring(minTop), table.concat(fl,','))
end)

run_chunk(29, function()
local names={'CastleGateArch','CastleGateVault','CastleGatePassage','CastleGateLintel','CastlePortcullisBars','CastleDrawbridgeWood','CastleTowerGateN','CastleTowerGateS','CastleGatehouseFloor','CastleGatehouseRoof','CastleRubble3','CastleRampPlanks','CastleStairway','CastleKeepDoorPostL','CastleCurtainN','CastleGateBannerN','CastleCourtyard','CastleMainGround','CastleMoatWater','CastlePondWater','CastleKeepMain','CastleTowerNW','CastleRetainingN','CastleFieldGround'}
for _,n in ipairs(names) do local o=workspace:FindFirstChild(n) if o then local p=o.Position local s=o.Size print(string.format('%s pos(%.1f,%.1f,%.1f) size(%.1f,%.1f,%.1f)',n,p.X,p.Y,p.Z,s.X,s.Y,s.Z)) else print(n..' MISSING') end end
return 'ok'
end)

run_chunk(30, function()
local names={'CastleGateArch','CastleGateVault','CastleGatePassage','CastleGateLintel','CastlePortcullisBars','CastleDrawbridgeWood','CastleTowerGateN','CastleTowerGateS','CastleGatehouseFloor','CastleGatehouseRoof','CastleRampPlanks','CastleKeepDoorPostL','CastleCurtainN','CastleCourtyard','CastleKeepMain','CastleTowerNW','CastleRetainingN','CastleStairway'}
local out={}
for _,n in ipairs(names) do local o=workspace:FindFirstChild(n) if o then local p=o.Position local s=o.Size table.insert(out,string.format('%s (%.1f,%.1f,%.1f) sz(%.1f,%.1f,%.1f)',n,p.X,p.Y,p.Z,s.X,s.Y,s.Z)) else table.insert(out,n..' MISSING') end end
return table.concat(out,' | ')
end)

run_chunk(31, function()
local out={}
for _,d in ipairs(workspace:GetDescendants()) do if d:IsA('Part') then table.insert(out, d.Name) end end
table.sort(out)
return #out..' :: '..table.concat(out, ', ')
end)

run_chunk(32, function()
local names={'CastleGround','CastleCurtainW','CastleCurtainE','CastleCurtainNW','CastleCurtainNE','CastleCurtainSW','CastleCurtainSE','CastleCopingS','CastleCopingW','CastleRoadMain','CastlePondWater','CastleGardenSoil','CastleTentAx','CastleWellRing1','CastleMerlonN1','CastleStallPost3'}
local out={}
for _,n in ipairs(names) do local o=workspace:FindFirstChild(n) if o then local p=o.Position local s=o.Size table.insert(out,string.format('%s (%.1f,%.1f,%.1f) sz(%.1f,%.1f,%.1f)',n,p.X,p.Y,p.Z,s.X,s.Y,s.Z)) else table.insert(out,n..' MISSING') end end
return table.concat(out,' | ')
end)

run_chunk(33, function()
local function P(n,s,c,m,col,sh)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(232,230,222) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block local CY=Enum.PartType.Cylinder local W=Enum.PartType.Wedge local CW=Enum.PartType.CornerWedge local BA=Enum.PartType.Ball
local COB=Enum.Material.Cobblestone local SL=Enum.Material.Slate local GR=Enum.Material.Granite
local ir=Enum.Material.CorrodedMetal
-- gatehouse flanking pillars (passage 8 wide, 7 tall)
P('CastleGatePillarW', Vector3.new(5,12,4.6), CFrame.new(-6.5,6,29.5), COB, Color3.fromRGB(228,226,216), B)
P('CastleGatePillarE', Vector3.new(5,12,4.6), CFrame.new(6.5,6,29.5), COB, Color3.fromRGB(226,224,214), B)
P('CastleGatePillarWBatter', Vector3.new(5.8,2.6,5.4), CFrame.new(-6.5,1.3,29.5), GR, Color3.fromRGB(220,218,210), B)
P('CastleGatePillarEBatter', Vector3.new(5.8,2.6,5.4), CFrame.new(6.5,1.3,29.5), GR, Color3.fromRGB(218,216,208), B)
P('CastleGateLintel', Vector3.new(8.8,1.5,4.3), CFrame.new(0,7.6,29.5), SL, Color3.fromRGB(228,226,218), B)
P('CastleGateArchW', Vector3.new(4.2,2.8,4.3), CFrame.new(-5.1,10.0,29.5)*CFrame.Angles(0,0,math.rad(-18)), CW, Color3.fromRGB(226,224,216), CW)
P('CastleGateArchE', Vector3.new(4.2,2.8,4.3), CFrame.new(5.1,10.0,29.5)*CFrame.Angles(0,math.rad(180),math.rad(18)), CW, Color3.fromRGB(224,222,214), CW)
P('CastleGateHouseUpper', Vector3.new(17.4,5,5.6), CFrame.new(0,14.4,29.5), COB, Color3.fromRGB(230,228,218), B)
P('CastleGateHouseBand', Vector3.new(18.2,0.9,6.2), CFrame.new(0,12.1,29.5), Enum.Material.Limestone, Color3.fromRGB(232,230,222), B)
P('CastleGateMerlonW1', Vector3.new(1.6,1.8,1.6), CFrame.new(-7.4,17.8,28.2), COB, Color3.fromRGB(224,222,212), B)
P('CastleGateMerlonW2', Vector3.new(1.6,1.8,1.6), CFrame.new(-7.4,17.8,30.8), COB, Color3.fromRGB(226,224,214), B)
P('CastleGateMerlonM1', Vector3.new(1.6,1.8,1.6), CFrame.new(0,17.8,28.2), COB, Color3.fromRGB(222,220,210), B)
P('CastleGateMerlonM2', Vector3.new(1.6,1.8,1.6), CFrame.new(0,17.8,30.8), COB, Color3.fromRGB(228,226,216), B)
P('CastleGateMerlonE1', Vector3.new(1.6,1.8,1.6), CFrame.new(7.4,17.8,28.2), COB, Color3.fromRGB(226,224,214), B)
P('CastleGateMerlonE2', Vector3.new(1.6,1.8,1.6), CFrame.new(7.4,17.8,30.8), COB, Color3.fromRGB(224,222,212), B)
-- portcullis bars (thin, hang in the passage)
P('CastleGatePortBar1', Vector3.new(6.6,0.32,0.32), CFrame.new(-2.4,3.3,27.2)*CFrame.Angles(0,0,math.rad(90)), ir, Color3.fromRGB(214,212,204), CY)
P('CastleGatePortBar2', Vector3.new(6.6,0.32,0.32), CFrame.new(0,3.3,27.2)*CFrame.Angles(0,0,math.rad(90)), ir, Color3.fromRGB(212,210,202), CY)
P('CastleGatePortBar3', Vector3.new(6.6,0.32,0.32), CFrame.new(2.4,3.3,27.2)*CFrame.Angles(0,0,math.rad(90)), ir, Color3.fromRGB(216,214,206), CY)
report('gatehouse with arch, battlements, portcullis')
end)

run_chunk(34, function()
local function P(n,s,c,m,sh)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block local CW=Enum.PartType.CornerWedge local CY=Enum.PartType.Cylinder
P('CastleGatePillarW', Vector3.new(5,12,4.6), CFrame.new(-6.5,6,29.5), Enum.Material.Cobblestone, B)
P('CastleGatePillarE', Vector3.new(5,12,4.6), CFrame.new(6.5,6,29.5), Enum.Material.Cobblestone, B)
P('CastleGatePillarWBatter', Vector3.new(5.8,2.6,5.4), CFrame.new(-6.5,1.3,29.5), Enum.Material.Granite, B)
P('CastleGatePillarEBatter', Vector3.new(5.8,2.6,5.4), CFrame.new(6.5,1.3,29.5), Enum.Material.Granite, B)
P('CastleGateLintel', Vector3.new(8.8,1.5,4.3), CFrame.new(0,7.6,29.5), Enum.Material.Slate, B)
P('CastleGateArchW', Vector3.new(4.2,2.8,4.3), CFrame.new(-5.1,10.0,29.5), Enum.Material.Slate, CW)
P('CastleGateArchE', Vector3.new(4.2,2.8,4.3), CFrame.new(5.1,10.0,29.5)*CFrame.Angles(0,math.rad(180),0), Enum.Material.Slate, CW)
P('CastleGateHouseUpper', Vector3.new(17.4,5,5.6), CFrame.new(0,14.4,29.5), Enum.Material.Cobblestone, B)
P('CastleGateHouseBand', Vector3.new(18.2,0.9,6.2), CFrame.new(0,12.1,29.5), Enum.Material.Limestone, B)
P('CastleGateMerlonW1', Vector3.new(1.6,1.8,1.6), CFrame.new(-7.4,17.8,28.2), Enum.Material.Cobblestone, B)
P('CastleGateMerlonW2', Vector3.new(1.6,1.8,1.6), CFrame.new(-7.4,17.8,30.8), Enum.Material.Cobblestone, B)
P('CastleGateMerlonM1', Vector3.new(1.6,1.8,1.6), CFrame.new(0,17.8,28.2), Enum.Material.Cobblestone, B)
P('CastleGateMerlonM2', Vector3.new(1.6,1.8,1.6), CFrame.new(0,17.8,30.8), Enum.Material.Cobblestone, B)
P('CastleGateMerlonE1', Vector3.new(1.6,1.8,1.6), CFrame.new(7.4,17.8,28.2), Enum.Material.Cobblestone, B)
P('CastleGateMerlonE2', Vector3.new(1.6,1.8,1.6), CFrame.new(7.4,17.8,30.8), Enum.Material.Cobblestone, B)
P('CastleGatePortBar1', Vector3.new(6.6,0.32,0.32), CFrame.new(-2.4,3.3,27.2), Enum.Material.CorrodedMetal, CY)
P('CastleGatePortBar2', Vector3.new(6.6,0.32,0.32), CFrame.new(0,3.3,27.2), Enum.Material.CorrodedMetal, CY)
P('CastleGatePortBar3', Vector3.new(6.6,0.32,0.32), CFrame.new(2.4,3.3,27.2), Enum.Material.CorrodedMetal, CY)
report('gatehouse built')
end)

run_chunk(35, function()
local function P(n,s,c,m,sh,col)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block local CY=Enum.PartType.Cylinder local W=Enum.PartType.Wedge local CW=Enum.PartType.CornerWedge local BA=Enum.PartType.Ball
local COB=Enum.Material.Cobblestone local BR=Enum.Material.Brick local SL=Enum.Material.Slate local LI=Enum.Material.Limestone local WO=Enum.Material.Wood
-- keep body 18 x 22 x 16 centred at (-4,-14)
P('CastleKeepMain', Vector3.new(18,22,16), CFrame.new(-4,11,-14), COB, B)
P('CastleKeepCourse1', Vector3.new(18.6,0.8,16.6), CFrame.new(-4,4,-14), LI, B)
P('CastleKeepCourse2', Vector3.new(18.5,0.7,16.5), CFrame.new(-4,15,-14), LI, B)
P('CastleKeepCourse3', Vector3.new(18.4,0.6,16.4), CFrame.new(-4,20.5,-14), LI, B)
P('CastleKeepPlinth', Vector3.new(19.6,2.2,17.6), CFrame.new(-4,1.1,-14), Enum.Material.Granite, B)
-- keep parapet merlons on the front edge
P('CastleKeepMerlon1', Vector3.new(1.7,2,1.4), CFrame.new(-11.5,23,-6.1), COB, B)
P('CastleKeepMerlon2', Vector3.new(1.7,2,1.4), CFrame.new(-7.2,23,-6.1), COB, B)
P('CastleKeepMerlon3', Vector3.new(1.7,2,1.4), CFrame.new(-2.9,23,-6.1), COB, B)
P('CastleKeepMerlon4', Vector3.new(1.7,2,1.4), CFrame.new(1.4,23,-6.1), COB, B)
-- corner turrets with cone roofs
P('CastleKeepTurretW', Vector3.new(6,29,6), CFrame.new(-12,14.5,-22), COB, CY)
P('CastleKeepTurretE', Vector3.new(6,29,6), CFrame.new(4,14.5,-22), Enum.Material.Brick, CY)
P('CastleKeepTurretWCornice', Vector3.new(7,1.2,7), CFrame.new(-12,29.4,-22), LI, CY)
P('CastleKeepTurretECornice', Vector3.new(7,1.2,7), CFrame.new(4,29.4,-22), LI, CY)
P('CastleKeepTurretWRoof', Vector3.new(7.4,6.6,7.4), CFrame.new(-12,33.2,-22), SL, CW)
P('CastleKeepTurretERoof', Vector3.new(7.4,6.6,7.4), CFrame.new(4,33.2,-22)*CFrame.Angles(0,math.rad(180),0), Enum.Material.ClayRoofTiles, CW)
P('CastleKeepTurretWFinial', Vector3.new(1.1,1.1,1.1), CFrame.new(-12,37.2,-22), Enum.Material.Metal, BA)
P('CastleKeepTurretEFinial', Vector3.new(1.1,1.1,1.1), CFrame.new(4,37.2,-22), Enum.Material.Metal, BA)
-- keep door and steps
P('CastleKeepDoor', Vector3.new(4.6,5.6,0.6), CFrame.new(-4,3.2,-5.7), WO, B)
P('CastleKeepDoorFrame', Vector3.new(5.8,1,0.9), CFrame.new(-4,6.4,-5.7), LI, B)
P('CastleKeepStep1', Vector3.new(6,0.8,1.6), CFrame.new(-4,0.4,-5.2), Enum.Material.Pavement, B)
P('CastleKeepStep2', Vector3.new(6,0.8,1.6), CFrame.new(-4,0.4,-3.6), Enum.Material.Pavement, B)
report('keep with turrets and cone roofs')
end)

run_chunk(36, function()
local function P(n,s,c,m,sh,col)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(230,228,220) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block local CY=Enum.PartType.Cylinder local W=Enum.PartType.Wedge local CW=Enum.PartType.CornerWedge local BA=Enum.PartType.Ball
local COB=Enum.Material.Cobblestone local LI=Enum.Material.Limestone local SL=Enum.Material.Slate local WO=Enum.Material.WoodPlanks local GR=Enum.Material.Granite
-- corner towers of the curtain (4), tall enough to clear wall crest 10
P('CastleTowerNW', Vector3.new(8.4,17,8.4), CFrame.new(-29.5,8.5,-25.5), COB, CY)
P('CastleTowerNE', Vector3.new(8.4,16,8.4), CFrame.new(29.5,8,-25.5), COB, CY)
P('CastleTowerSW', Vector3.new(8.4,18,8.4), CFrame.new(-29.5,9,26.5), COB, CY)
P('CastleTowerSE', Vector3.new(8.4,15,8.4), CFrame.new(30.5,7.5,26.5), COB, CY)
P('CastleTowerNWCap', Vector3.new(9.6,1.3,9.6), CFrame.new(-29.5,17.6,-25.5), LI, CY)
P('CastleTowerNECap', Vector3.new(9.6,1.3,9.6), CFrame.new(29.5,17,-25.5), LI, CY)
P('CastleTowerSWCap', Vector3.new(9.6,1.3,9.6), CFrame.new(-29.5,18.6,26.5), LI, CY)
P('CastleTowerSECap', Vector3.new(9.6,1.3,9.6), CFrame.new(30.5,15.6,26.5), LI, CY)
P('CastleTowerNWRoof', Vector3.new(9.6,7.4,9.6), CFrame.new(-29.5,21.9,-25.5), SL, CW)
P('CastleTowerNERoof', Vector3.new(9.6,7.2,9.6), CFrame.new(29.5,21.3,-25.5)*CFrame.Angles(0,math.rad(180),0), Enum.Material.ClayRoofTiles, CW)
P('CastleTowerSWRoof', Vector3.new(9.6,7.8,9.6), CFrame.new(-29.5,22.8,26.5), Enum.Material.RoofShingles, CW)
P('CastleTowerSERoof', Vector3.new(9.6,7,9.6), CFrame.new(30.5,19.1,26.5)*CFrame.Angles(0,math.rad(180),0), Enum.Material.Slate, CW)
P('CastleTowerNWBanner', Vector3.new(1,4.6,3), CFrame.new(-24.9,12,-25.5), Enum.Material.Fabric, B, Color3.fromRGB(160,40,50))
P('CastleTowerNEBanner', Vector3.new(1,4.6,3), CFrame.new(24.9,12,-25.5), Enum.Material.Fabric, B, Color3.fromRGB(40,70,150))
P('CastleTowerSWBanner', Vector3.new(1,4.6,3), CFrame.new(-24.9,12.5,26.5), Enum.Material.Fabric, B, Color3.fromRGB(150,120,40))
-- gate flanking towers (touch the gatehouse)
P('CastleGateTowerW', Vector3.new(7.4,20,7.4), CFrame.new(-13.4,10,29.5), Enum.Material.Brick, CY)
P('CastleGateTowerE', Vector3.new(7.4,18,7.4), CFrame.new(13.4,9,29.5), COB, CY)
P('CastleGateTowerWCap', Vector3.new(8.4,1.2,8.4), CFrame.new(-13.4,20.6,29.5), LI, CY)
P('CastleGateTowerECap', Vector3.new(8.4,1.2,8.4), CFrame.new(13.4,18.6,29.5), LI, CY)
P('CastleGateTowerWRoof', Vector3.new(8.4,6.6,8.4), CFrame.new(-13.4,24.5,29.5), SL, CW)
P('CastleGateTowerERoof', Vector3.new(8.4,6.4,8.4), CFrame.new(13.4,22.4,29.5)*CFrame.Angles(0,math.rad(180),0), Enum.Material.ClayRoofTiles, CW)
report('six towers with roofs')
end)

run_chunk(37, function()
local function P(n,s,c,m,sh,col)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(232,230,222) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block local CY=Enum.PartType.Cylinder local W=Enum.PartType.Wedge local BA=Enum.PartType.Ball
local COB=Enum.Material.Cobblestone local WO=Enum.Material.WoodPlanks local LI=Enum.Material.Limestone local PAV=Enum.Material.Pavement
-- courtyard paving
P('CastleCourtyardPaving', Vector3.new(58,0.2,58), CFrame.new(0,0.11,-1), PAV, B)
-- well
P('CastleWellRing1', Vector3.new(5,1.4,4.2), CFrame.new(14,0.7,10), COB, CY)
P('CastleWellWater', Vector3.new(3.6,0.4,3.6), CFrame.new(14,1.25,10), Enum.Material.Glass, BA, Color3.fromRGB(120,160,190))
P('CastleWellPostW', Vector3.new(0.6,5.4,0.6), CFrame.new(11.9,2.9,10), WO, B)
P('CastleWellPostE', Vector3.new(0.6,5.4,0.6), CFrame.new(16.1,2.9,10), WO, B)
P('CastleWellBeam', Vector3.new(4.8,0.5,0.5), CFrame.new(14,5.3,10), Enum.Material.Wood, B)
P('CastleWellRoofW', Vector3.new(5.6,3.4,3.2), CFrame.new(14,7.3,8.4)*CFrame.Angles(math.rad(38),0,0), Enum.Material.RoofShingles, W)
P('CastleWellRoofE', Vector3.new(5.6,3.4,3.2), CFrame.new(14,7.3,11.6)*CFrame.Angles(math.rad(-38),0,0), Enum.Material.RoofShingles, W)
-- campfire
P('CastleCampfireRing', Vector3.new(4.4,0.7,4.4), CFrame.new(-14,0.35,12), Enum.Material.Rock, CY)
P('CastleCampfireLogA', Vector3.new(3.4,0.7,0.7), CFrame.new(-14,0.9,12), Enum.Material.Wood, CY)
P('CastleCampfireLogB', Vector3.new(3.4,0.7,0.7), CFrame.new(-14,0.9,12)*CFrame.Angles(0,math.rad(70),0), Enum.Material.Wood, CY)
P('CastleCampfireFlame', Vector3.new(2.4,3,2.4), CFrame.new(-14,2.4,12), Enum.Material.Neon, BA, Color3.fromRGB(255,150,40))
P('CastleCampfireSmoke', Vector3.new(2.6,4,2.6), CFrame.new(-14,6,12), Enum.Material.ForceField, BA, Color3.fromRGB(150,150,160))
-- market stall
P('CastleStallCounter', Vector3.new(6,1,3), CFrame.new(16,1,-12), WO, B)
P('CastleStallLegA', Vector3.new(0.6,1.4,0.6), CFrame.new(13.4,0.7,-13), WO, B)
P('CastleStallLegB', Vector3.new(0.6,1.4,0.6), CFrame.new(18.6,0.7,-11), Enum.Material.Wood, B)
P('CastleStallPostA', Vector3.new(0.5,6.6,0.5), CFrame.new(13.2,3.3,-13), WO, B)
P('CastleStallPostB', Vector3.new(0.5,6.6,0.5), CFrame.new(18.8,3.3,-13), WO, B)
P('CastleStallCanopyW', Vector3.new(6.4,3.2,3.6), CFrame.new(16,7,-14.6)*CFrame.Angles(math.rad(30),0,0), Enum.Material.Fabric, W, Color3.fromRGB(190,70,70))
P('CastleStallCanopyE', Vector3.new(6.4,3.2,3.6), CFrame.new(16,7,-11.4)*CFrame.Angles(math.rad(-30),0,0), Enum.Material.Fabric, W, Color3.fromRGB(190,70,70))
P('CastleStallCrate', Vector3.new(1.6,1.6,1.6), CFrame.new(18.4,0.8,-11.2), WO, B)
report('courtyard: well, campfire, stall')
end)

run_chunk(38, function()
local function P(n,s,c,m,sh,col)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(232,230,222) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block local CY=Enum.PartType.Cylinder local W=Enum.PartType.Wedge local BA=Enum.PartType.Ball
local COB=Enum.Material.Cobblestone local LI=Enum.Material.Limestone local WO=Enum.Material.WoodPlanks
-- moat flanking the causeway
P('CastleMoatW', Vector3.new(56,0.5,10), CFrame.new(-35,0.25,43), Enum.Material.Glass, B, Color3.fromRGB(150,185,200))
P('CastleMoatE', Vector3.new(56,0.5,10), CFrame.new(35,0.25,43), Enum.Material.Glass, B, Color3.fromRGB(150,185,200))
P('CastleMoatBankW1', Vector3.new(57,0.9,1.4), CFrame.new(-35,0.45,37.4), COB, B)
P('CastleMoatBankE1', Vector3.new(57,0.9,1.4), CFrame.new(35,0.45,37.4), COB, B)
P('CastleMoatBankW2', Vector3.new(57,0.9,1.4), CFrame.new(-35,0.45,48.6), LI, B)
P('CastleMoatBankE2', Vector3.new(57,0.9,1.4), CFrame.new(35,0.45,48.6), LI, B)
P('CastleRoadCurbW', Vector3.new(1.2,0.7,30), CFrame.new(-6.2,0.35,49), Enum.Material.Pavement, B)
P('CastleRoadCurbE', Vector3.new(1.2,0.7,30), CFrame.new(6.2,0.35,49), Enum.Material.Pavement, B)
-- tents outside the walls
P('CastleTentBodyA', Vector3.new(9,5,8), CFrame.new(44,2.5,-14), Enum.Material.Fabric, B, Color3.fromRGB(170,150,110))
P('CastleTentRoofAW', Vector3.new(9.6,3.4,4.4), CFrame.new(44,6.7,-16.1)*CFrame.Angles(math.rad(35),0,0), Enum.Material.Fabric, W, Color3.fromRGB(190,160,110))
P('CastleTentRoofAE', Vector3.new(9.6,3.4,4.4), CFrame.new(44,6.7,-11.9)*CFrame.Angles(math.rad(-35),0,0), Enum.Material.Fabric, W, Color3.fromRGB(190,160,110))
P('CastleTentPoleA', Vector3.new(0.5,10,0.5), CFrame.new(44,5,-14), WO, B)
P('CastleTentBodyB', Vector3.new(7,4.4,7), CFrame.new(-45,2.2,16), Enum.Material.Fabric, B, Color3.fromRGB(120,140,120))
P('CastleTentRoofBW', Vector3.new(7.6,3,3.8), CFrame.new(-45,5.9,14.2)*CFrame.Angles(math.rad(36),0,0), Enum.Material.Fabric, W, Color3.fromRGB(140,160,140))
P('CastleTentRoofBE', Vector3.new(7.6,3,3.8), CFrame.new(-45,5.9,17.8)*CFrame.Angles(math.rad(-36),0,0), Enum.Material.Fabric, W, Color3.fromRGB(140,160,140))
-- trees
P('CastleTreeTrunkA', Vector3.new(1.8,7,1.8), CFrame.new(50,3.5,6), Enum.Material.Wood, B)
P('CastleTreeCanopyA', Vector3.new(7,7,7), CFrame.new(50,9.5,6), Enum.Material.LeafyGrass, BA)
P('CastleTreeTrunkB', Vector3.new(1.6,6,1.6), CFrame.new(-50,3,-12), Enum.Material.Wood, B)
P('CastleTreeCanopyB', Vector3.new(6,6,6), CFrame.new(-50,8.5,-12), Enum.Material.Grass, BA)
P('CastleTreeTrunkC', Vector3.new(1.5,5.4,1.5), CFrame.new(22,2.7,-48), Enum.Material.WoodPlanks, B)
P('CastleTreeCanopyC', Vector3.new(5.4,5.4,5.4), CFrame.new(22,7.9,-48), Enum.Material.LeafyGrass, BA)
P('CastleBoulderA', Vector3.new(5,4,5), CFrame.new(-56,1.6,34), Enum.Material.Rock, BA)
P('CastleBoulderB', Vector3.new(4,3.4,4), CFrame.new(52,1.4,-40), Enum.Material.Basalt, BA)
report('moat, causeway, tents, trees, boulders')
end)

run_chunk(39, function()
local function P(n,s,c,m,sh,col)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(232,230,222) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block local CY=Enum.PartType.Cylinder local W=Enum.PartType.Wedge local BA=Enum.PartType.Ball
local WO=Enum.Material.WoodPlanks local LIM=Enum.Material.Limestone
-- stair up to the west wall walk
P('CastleWallStair1', Vector3.new(4,0.8,1.6), CFrame.new(-21,0.4,-2), Enum.Material.Pavement, W)
P('CastleWallStair2', Vector3.new(4,1.6,1.6), CFrame.new(-21,0.8,-3.6), Enum.Material.Pavement, W)
P('CastleWallStair3', Vector3.new(4,2.4,1.6), CFrame.new(-21,1.2,-5.2), Enum.Material.Pavement, W)
P('CastleWallStair4', Vector3.new(4,3.2,1.6), CFrame.new(-21,1.6,-6.8), Enum.Material.Pavement, W)
P('CastleWallStair5', Vector3.new(4,4,1.6), CFrame.new(-21,2,-8.4), Enum.Material.Pavement, W)
-- barrels and crates around the gate
P('CastleBarrelA', Vector3.new(1.8,2.4,1.8), CFrame.new(-10,1.2,24), WO, CY)
P('CastleBarrelB', Vector3.new(1.8,2.4,1.8), CFrame.new(-8.2,1.2,25.4), Enum.Material.Wood, CY)
P('CastleBarrelC', Vector3.new(1.6,2.1,1.6), CFrame.new(9.4,1.05,23.4), WO, CY)
P('CastleCrateA', Vector3.new(1.8,1.8,1.8), CFrame.new(11.4,0.9,24.6), WO, B)
P('CastleCrateB', Vector3.new(1.4,1.4,1.4), CFrame.new(11.2,0.7,26.4), WO, B)
P('CastleCrateC', Vector3.new(1.6,1.6,1.6), CFrame.new(-11.6,0.8,25.2), Enum.Material.Wood, B)
-- hay cart
P('CastleCartBed', Vector3.new(3.4,0.8,6), CFrame.new(22,1.4,-2), WO, B)
P('CastleCartWheelW', Vector3.new(2.4,2.4,0.5), CFrame.new(22,1.2,-0.6), Enum.Material.Wood, CY)
P('CastleCartWheelE', Vector3.new(2.4,2.4,0.5), CFrame.new(22,1.2,-3.4), Enum.Material.Wood, CY)
P('CastleCartHay', Vector3.new(3.2,1.6,5.4), CFrame.new(22,2.6,-2), Enum.Material.Grass, B, Color3.fromRGB(220,200,140))
P('CastleCartShaft', Vector3.new(0.4,0.4,4), CFrame.new(22,1.4,1.6), WO, B)
-- benches
P('CastleBenchSeat', Vector3.new(4.6,0.4,1.3), CFrame.new(-4,1.1,16), WO, B)
P('CastleBenchLegA', Vector3.new(0.5,0.9,1.1), CFrame.new(-5.8,0.45,16), Enum.Material.Wood, B)
P('CastleBenchLegB', Vector3.new(0.5,0.9,1.1), CFrame.new(-2.2,0.45,16), Enum.Material.Wood, B)
-- garden inside the walls
P('CastleGardenSoil', Vector3.new(14,0.4,8), CFrame.new(-20,0.2,16), Enum.Material.Mud, B)
P('CastleGardenHedgeA', Vector3.new(14,1.8,1.2), CFrame.new(-20,0.9,20), Enum.Material.LeafyGrass, B)
P('CastleGardenHedgeB', Vector3.new(1.2,1.8,8), CFrame.new(-26.5,0.9,16), Enum.Material.LeafyGrass, B)
P('CastleGardenCrop1', Vector3.new(3.6,2.4,3), CFrame.new(-23,1.2,14), Enum.Material.Grass, B, Color3.fromRGB(200,210,160))
P('CastleGardenCrop2', Vector3.new(3.6,2,3), CFrame.new(-17,1,18), Enum.Material.Grass, B, Color3.fromRGB(190,205,150))
report('stairs, barrels, cart, benches, garden')
end)

run_chunk(40, function()
local function P(n,s,c,m,sh,col)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(232,230,222) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block local CY=Enum.PartType.Cylinder local W=Enum.PartType.Wedge local BA=Enum.PartType.Ball local CW=Enum.PartType.CornerWedge
-- chapel / hall inside the east courtyard
P('CastleChapelBody', Vector3.new(13,11,9), CFrame.new(19,5.5,-15), Enum.Material.Limestone, B)
P('CastleChapelPlinth', Vector3.new(14,1.4,10), CFrame.new(19,0.7,-15), Enum.Material.Granite, B)
P('CastleChapelRoofW', Vector3.new(13.4,3.6,5.4), CFrame.new(19,12.6,-17.4)*CFrame.Angles(math.rad(36),0,0), Enum.Material.RoofShingles, W)
P('CastleChapelRoofE', Vector3.new(13.4,3.6,5.4), CFrame.new(19,12.6,-12.6)*CFrame.Angles(math.rad(-36),0,0), Enum.Material.RoofShingles, W)
P('CastleChapelBellTower', Vector3.new(5,17,5), CFrame.new(25.4,8.5,-15), Enum.Material.Brick, CY)
P('CastleChapelBellCap', Vector3.new(6,1,6), CFrame.new(25.4,17.4,-15), Enum.Material.Limestone, CY)
P('CastleChapelSpire', Vector3.new(5.6,6.4,5.6), CFrame.new(25.4,21,-15), Enum.Material.Slate, CW)
P('CastleChapelSpireBall', Vector3.new(1.4,1.4,1.4), CFrame.new(25.4,24.6,-15), Enum.Material.Metal, BA)
P('CastleChapelDoor', Vector3.new(0.5,5.2,3.4), CFrame.new(12.4,2.9,-15), Enum.Material.Wood, B)
P('CastleChapelWindowA', Vector3.new(0.4,2.6,1.6), CFrame.new(12.4,7,-19), Enum.Material.Glass, B, Color3.fromRGB(120,150,200))
P('CastleChapelWindowB', Vector3.new(0.4,2.6,1.6), CFrame.new(12.4,7,-11), Enum.Material.Glass, B, Color3.fromRGB(120,150,200))
-- keep banner and window slits
P('CastleKeepBanner', Vector3.new(0.6,6,4), CFrame.new(-4,15,-5.5), Enum.Material.Fabric, B, Color3.fromRGB(150,40,60))
P('CastleKeepSlitA', Vector3.new(0.5,2.4,1), CFrame.new(-10,17,-5.7), Enum.Material.Slate, B)
P('CastleKeepSlitB', Vector3.new(0.5,2.4,1), CFrame.new(2,17,-5.7), Enum.Material.Slate, B)
-- gate torches
P('CastleGateTorchW', Vector3.new(0.5,1.8,0.5), CFrame.new(-9.3,5.2,27.2), Enum.Material.Metal, CY)
P('CastleGateTorchE', Vector3.new(0.5,1.8,0.5), CFrame.new(9.3,5.2,27.2), Enum.Material.Metal, CY)
P('CastleGateFlameW', Vector3.new(1.2,1.6,1.2), CFrame.new(-9.3,6.6,27.2), Enum.Material.Neon, BA, Color3.fromRGB(255,170,60))
P('CastleGateFlameE', Vector3.new(1.2,1.6,1.2), CFrame.new(9.3,6.6,27.2), Enum.Material.Neon, BA, Color3.fromRGB(255,170,60))
report('chapel, banners, torches')
end)

run_chunk(41, function()
local function P(n,s,c,m,sh,col)
  local p = Instance.new('Part') p.Name=n p.Size=s p.CFrame=c p.Material=m
  p.Color = col or Color3.fromRGB(232,230,222) p.Shape = sh or Enum.PartType.Block
  p.Anchored=true p.Parent=workspace return p
end
local B=Enum.PartType.Block local CY=Enum.PartType.Cylinder local BA=Enum.PartType.Ball
P('CastleApproachDirt', Vector3.new(72,0.3,12), CFrame.new(0,0.15,55), Enum.Material.Ground, B)
P('CastleFencePostA1', Vector3.new(0.6,3,0.6), CFrame.new(-9,1.5,38), Enum.Material.Wood, B)
P('CastleFencePostA2', Vector3.new(0.6,3,0.6), CFrame.new(-9,1.5,44), Enum.Material.Wood, B)
P('CastleFencePostA3', Vector3.new(0.6,3,0.6), CFrame.new(-9,1.5,50), Enum.Material.Wood, B)
P('CastleFencePostB1', Vector3.new(0.6,3,0.6), CFrame.new(9,1.5,41), Enum.Material.Wood, B)
P('CastleFencePostB2', Vector3.new(0.6,3,0.6), CFrame.new(9,1.5,47), Enum.Material.Wood, B)
P('CastleFenceRailA', Vector3.new(0.3,0.35,12), CFrame.new(-9,2.2,44), Enum.Material.WoodPlanks, B)
P('CastleFenceRailB', Vector3.new(0.3,0.35,8), CFrame.new(9,2.2,44), Enum.Material.WoodPlanks, B)
P('CastleRockA', Vector3.new(3,2.4,3), CFrame.new(-40,1.2,30), Enum.Material.Rock, BA)
P('CastleRockB', Vector3.new(2.4,2,2.4), CFrame.new(36,1,-34), Enum.Material.Basalt, BA)
P('CastleRockC', Vector3.new(2,1.6,2), CFrame.new(-38,0.8,-38), Enum.Material.Rock, BA)
P('CastleBushA', Vector3.new(3,2.6,3), CFrame.new(34,1.3,20), Enum.Material.LeafyGrass, BA)
P('CastleBushB', Vector3.new(2.4,2,2.4), CFrame.new(-34,1,8), Enum.Material.Grass, BA)
report('approach road, fences, rocks, bushes — castle complete')
end)

run_chunk(42, function()
local parts={}
for _,d in ipairs(workspace:GetDescendants()) do if d:IsA('Part') and string.sub(d.Name,1,6)=='Castle' then table.insert(parts,d) end end
local mats={} local shapes={} local n=0
for _,p in ipairs(parts) do n=n+1 mats[p.Material.Name]=true shapes[p.Shape.Name]=true end
local ml,sl={},{}
for k in pairs(mats) do table.insert(ml,k) end
for k in pairs(shapes) do table.insert(sl,k) end
table.sort(ml) table.sort(sl)
return string.format('parts=%d materials=%d [%s] shapes=[%s]', n, #ml, table.concat(ml,','), table.concat(sl,','))
end)
