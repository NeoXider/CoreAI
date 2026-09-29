local root = Instance.new('Folder')
root.Name = 'CoreAiParityRoot'
root.Parent = workspace

local original = Instance.new('Part')
original.Name = 'Probe'
original.Size = Vector3.new(4, 2, 6)
original.CFrame = CFrame.new(7, 3, -5)
original.Color = Color3.fromRGB(255, 128, 0)
original.Material = Enum.Material.Brick
original.Anchored = true
original.Parent = root

local clone = original:Clone()
clone.Name = 'ProbeCopy'
clone.CFrame = CFrame.new(-2, 4, 9)
clone.Parent = root
local before = #root:GetChildren()
original:Destroy()
local after = #root:GetChildren()

assert(root:FindFirstChild('Probe') == nil, 'Destroy did not remove original')
assert(root:FindFirstChild('ProbeCopy') == clone, 'Clone not found')
assert(clone.Material == Enum.Material.Brick, 'Material did not clone')
assert(clone.Anchored == true, 'Anchored did not clone')
assert(clone.Size.X == 4 and clone.Size.Y == 2 and clone.Size.Z == 6, 'Size did not clone')
assert(clone.Position.X == -2 and clone.Position.Y == 4 and clone.Position.Z == 9,
    'CFrame did not set clone Position')

local summary = 'before=' .. tostring(before) .. ';after=' .. tostring(after) ..
    ';clone=' .. clone.Name .. ';position=' .. tostring(clone.Position.X) .. ',' ..
    tostring(clone.Position.Y) .. ',' .. tostring(clone.Position.Z)
assert(summary == 'before=2;after=1;clone=ProbeCopy;position=-2,4,9', summary)
if store_set then store_set('summary', summary) end
print('COREAI_ROBLOX_PARITY|' .. summary)
root:Destroy()
assert(workspace:FindFirstChild('CoreAiParityRoot') == nil, 'Root Destroy failed')
