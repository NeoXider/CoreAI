local previous = workspace:FindFirstChild('CastleQuestParity')
if previous then previous:Destroy() end

local castle = Instance.new('Model')
castle.Name = 'CastleQuestParity'
castle.Parent = workspace

local function brick(name, size, position, color, material, collision)
    local part = Instance.new('Part')
    part.Name = name
    part.Size = size
    part.Position = position
    part.Color = color
    part.Material = material
    part.Anchored = true
    part.CanCollide = collision
    part.Parent = castle
    return part
end

local stone = Color3.fromRGB(145, 150, 155)
local darkStone = Color3.fromRGB(74, 79, 88)
local timber = Color3.fromRGB(103, 65, 39)
local gold = Color3.fromRGB(255, 195, 34)
local green = Color3.fromRGB(66, 205, 111)
local red = Color3.fromRGB(232, 66, 62)
local metal = Color3.fromRGB(78, 87, 101)

brick('Ground', Vector3.new(56, 1, 66), Vector3.new(0, -1, -5),
    Color3.fromRGB(71, 125, 68), Enum.Material.Grass, true)
brick('Courtyard', Vector3.new(33, 1, 33), Vector3.new(0, 0, 0),
    darkStone, Enum.Material.Slate, true)
brick('WestWall', Vector3.new(2, 9, 36), Vector3.new(-18, 5, 0),
    stone, Enum.Material.Cobblestone, true)
brick('EastWall', Vector3.new(2, 9, 36), Vector3.new(18, 5, 0),
    stone, Enum.Material.Cobblestone, true)
brick('NorthWall', Vector3.new(36, 9, 2), Vector3.new(0, 5, 18),
    stone, Enum.Material.Cobblestone, true)
brick('SouthWallLeft', Vector3.new(14, 9, 2), Vector3.new(-11, 5, -18),
    stone, Enum.Material.Cobblestone, true)
brick('SouthWallRight', Vector3.new(14, 9, 2), Vector3.new(11, 5, -18),
    stone, Enum.Material.Cobblestone, true)

for _, x in ipairs({-18, 18}) do
    for _, z in ipairs({-18, 18}) do
        local tower = brick('Tower', Vector3.new(13, 6, 6), Vector3.new(x, 7, z),
            darkStone, Enum.Material.Brick, true)
        tower.Shape = Enum.PartType.Cylinder
        tower.CFrame = CFrame.new(x, 7, z) * CFrame.Angles(0, 0, math.rad(90))
        brick('TowerRoof', Vector3.new(7, 1, 7), Vector3.new(x, 14, z),
            timber, Enum.Material.Wood, true)
    end
end

for _, z in ipairs({-18, 18}) do
    for x = -13, 13, 4 do
        if z == 18 or x < -4 or x > 4 then
            brick('Battlement', Vector3.new(2, 2, 2), Vector3.new(x, 10.5, z),
                stone, Enum.Material.Cobblestone, true)
        end
    end
end
for _, x in ipairs({-18, 18}) do
    for z = -13, 13, 4 do
        brick('Battlement', Vector3.new(2, 2, 2), Vector3.new(x, 10.5, z),
            stone, Enum.Material.Cobblestone, true)
    end
end

brick('Bridge', Vector3.new(8, 1, 13), Vector3.new(0, 0, -25),
    timber, Enum.Material.Wood, true)
local gate = brick('Gate', Vector3.new(7, 8, 1), Vector3.new(0, 4.5, -18),
    metal, Enum.Material.Metal, true)
brick('GateLintel', Vector3.new(10, 2, 3), Vector3.new(0, 10, -18),
    darkStone, Enum.Material.Brick, true)
local hazard = brick('LavaTrap', Vector3.new(6, 0.3, 3), Vector3.new(0, 0.7, -24),
    red, Enum.Material.Neon, false)
local finish = brick('VictoryPad', Vector3.new(7, 0.3, 7), Vector3.new(0, 0.7, 8),
    green, Enum.Material.Neon, false)

local coins = {}
for index, x in ipairs({-11, 0, 11}) do
    local coin = brick('Coin' .. index, Vector3.new(1.5, 1.5, 0.4),
        Vector3.new(x, 2, -29), gold, Enum.Material.Metal, false)
    coins[index] = coin
end
local key = brick('Key', Vector3.new(2, 0.5, 0.7), Vector3.new(15, 2, -27),
    gold, Enum.Material.Metal, false)
local runner = brick('QuestRunner', Vector3.new(2, 3, 2), Vector3.new(0, 2, -32),
    Color3.fromRGB(58, 155, 240), Enum.Material.SmoothPlastic, false)

local state = {coins = 0, key = false, health = 3, gateOpen = false, won = false}
local function collectCoin(index, other)
    if other ~= runner or coins[index] == nil then return end
    coins[index]:Destroy()
    coins[index] = nil
    state.coins = state.coins + 1
end
local function collectKey(other)
    if other ~= runner or state.key then return end
    state.key = true
    key:Destroy()
end
local function touchHazard(other)
    if other ~= runner or state.health <= 0 then return end
    state.health = state.health - 1
end
local function tryGate(other)
    if other ~= runner or state.gateOpen then return end
    if state.coins == 3 and state.key and state.health > 0 then
        state.gateOpen = true
        gate.Position = Vector3.new(0, 14, -18)
        gate.CanCollide = false
        gate.Color = green
    end
end
local function reachFinish(other)
    if other == runner and state.gateOpen and state.health > 0 then
        state.won = true
        runner.Color = gold
    end
end

for index, coin in ipairs(coins) do
    local coinIndex = index
    coin.Touched:Connect(function(other) collectCoin(coinIndex, other) end)
end
key.Touched:Connect(collectKey)
hazard.Touched:Connect(touchHazard)
gate.Touched:Connect(tryGate)
finish.Touched:Connect(reachFinish)

local initialParts = #castle:GetChildren()
tryGate(runner)
assert(not state.gateOpen, 'Gate opened without collectibles')
reachFinish(runner)
assert(not state.won, 'Finish bypassed locked gate')
collectCoin(1, runner)
collectCoin(1, runner)
collectCoin(2, runner)
collectCoin(3, runner)
assert(state.coins == 3, 'Coin count or duplicate pickup mismatch')
tryGate(runner)
assert(not state.gateOpen, 'Gate opened without key')
collectKey(runner)
touchHazard(runner)
tryGate(runner)
assert(state.gateOpen and gate.CanCollide == false, 'Gate did not open')
reachFinish(runner)
assert(state.won and state.health == 2, 'Victory or damage mismatch')
runner.Position = Vector3.new(0, 2.5, 8)

local remainingParts = #castle:GetChildren()
assert(remainingParts == initialParts - 4, 'Collected objects remain in castle')
assert(castle:FindFirstChild('Key') == nil, 'Collected key remains in castle')
assert(castle:FindFirstChild('Coin1') == nil, 'Collected coin remains in castle')
assert(gate.Position.Y == 14, 'Gate did not rise to open position')

local summary = 'parts=' .. tostring(initialParts) .. '>' .. tostring(remainingParts) ..
    ';coins=' .. tostring(state.coins) .. ';key=' .. tostring(state.key) ..
    ';health=' .. tostring(state.health) .. ';gate=' .. tostring(state.gateOpen) ..
    ';win=' .. tostring(state.won) .. ';gateY=' .. tostring(gate.Position.Y)
if store_set then store_set('castleQuestSummary', summary) end
print('COREAI_CASTLE_QUEST_PARITY|' .. summary)
