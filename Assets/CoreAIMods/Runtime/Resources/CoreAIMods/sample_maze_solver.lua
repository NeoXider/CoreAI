--[[@coreai
id: sample_maze_solver
name: Lua Maze Solver
version: 1.0.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Pure Lua breadth-first path search on a 16 by 10 board. At most 24 cells are expanded per frame. Click the gold gate switch to close the passage and search again; existing board parts are reused.
]]
local WIDTH, HEIGHT, WORK_PER_FRAME = 16, 10, 24
local root = Instance.new("Folder")
root.Name = "LuaMazeSolver"
root.Parent = workspace
local cells, blocked = {}, {}
local gateClosed = false
local function key(x, y) return (y - 1) * WIDTH + x end
local gateKey = key(6, 3)
local startKey, goalKey = key(1, 1), key(WIDTH, HEIGHT)
local normalColor = Color3.fromRGB(35, 55, 80)
local wallColor = Color3.fromRGB(100, 110, 130)
local visitedColor = Color3.fromRGB(45, 110, 145)
local pathColor = Color3.fromRGB(40, 220, 210)
for y = 1, HEIGHT do
    for x = 1, WIDTH do
        local index = key(x, y)
        blocked[index] = (x == 6 and y ~= 3) or (x == 11 and y ~= 8)
        local cell = Instance.new("Part")
        cell.Name = "Cell" .. index
        cell.Anchored = true
        cell.Size = Vector3.new(1, blocked[index] and 1.5 or 0.15, 1)
        cell.Position = Vector3.new((x - 8.5) * 1.1, cell.Size.Y * 0.5, (y - 5.5) * 1.1)
        cell.Color = blocked[index] and wallColor or normalColor
        cell.Parent = root
        cells[index] = cell
    end
end
local queue, previous, seen = {}, {}, {}
local head, tail, expanded = 1, 1, 0
local done = false
local searches = 0
local function beginSearch()
    queue, previous, seen = {startKey}, {}, {[startKey] = true}
    head, tail, expanded, done = 1, 1, 0, false
    searches = searches + 1
    for index = 1, #cells do
        cells[index].Color = blocked[index] and wallColor or normalColor
    end
    cells[startKey].Color = Color3.fromRGB(40, 220, 110)
    cells[goalKey].Color = Color3.fromRGB(245, 110, 70)
    root:SetAttribute("Searches", searches)
    root:SetAttribute("Solved", false)
    root:SetAttribute("Finished", false)
    root:SetAttribute("Expanded", 0)
    root:SetAttribute("PathLength", 0)
    root:SetAttribute("GateOpen", not gateClosed)
    root:SetAttribute("WorkPerFrame", WORK_PER_FRAME)
end
local function visit(candidate, parent)
    if seen[candidate] or blocked[candidate] then return end
    seen[candidate] = true
    previous[candidate] = parent
    tail = tail + 1
    queue[tail] = candidate
end
local button = Instance.new("Part")
button.Name = "GateSwitch"
button.Anchored = true
button.Size = Vector3.new(2, 0.5, 2)
button.Position = Vector3.new(0, 1, -7.5)
button.Color = Color3.fromRGB(245, 185, 45)
button.Parent = root
local click = Instance.new("ClickDetector")
click.MaxActivationDistance = 96
click.Parent = button
click.MouseClick:Connect(function()
    gateClosed = not gateClosed
    blocked[gateKey] = gateClosed
    cells[gateKey].Size = Vector3.new(1, gateClosed and 1.5 or 0.15, 1)
    local position = cells[gateKey].Position
    cells[gateKey].Position = Vector3.new(position.X, cells[gateKey].Size.Y * 0.5, position.Z)
    beginSearch()
end)
beginSearch()
game:GetService("RunService").Heartbeat:Connect(function()
    if done then return end
    local work = 0
    while work < WORK_PER_FRAME and head <= tail do
        local current = queue[head]
        head = head + 1
        expanded = expanded + 1
        work = work + 1
        if current == goalKey then
            local length = 0
            local index = current
            while index do
                cells[index].Color = pathColor
                length = length + 1
                index = previous[index]
            end
            root:SetAttribute("Solved", true)
            root:SetAttribute("PathLength", length)
            done = true
            break
        end
        if current ~= startKey then cells[current].Color = visitedColor end
        local x = (current - 1) % WIDTH + 1
        local y = math.floor((current - 1) / WIDTH) + 1
        if x > 1 then visit(current - 1, current) end
        if x < WIDTH then visit(current + 1, current) end
        if y > 1 then visit(current - WIDTH, current) end
        if y < HEIGHT then visit(current + WIDTH, current) end
    end
    if head > tail then done = true end
    root:SetAttribute("Expanded", expanded)
    root:SetAttribute("LastFrameWork", work)
    root:SetAttribute("Finished", done)
end)
