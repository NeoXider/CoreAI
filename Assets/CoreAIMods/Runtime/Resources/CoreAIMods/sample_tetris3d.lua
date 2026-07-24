--[[@coreai
id: sample_tetris3d
name: Tetris 3D (sample)
version: 1.1.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Opt-in playable sample. A compact falling-block puzzle rendered with 3D cubes - A/D move, W rotate, S soft-drop; full rows clear. Uses only the standard Roblox-style API (Instance.new, Vector3, Color3, UserInputService) plus a game-loop timer. Ships disabled; enable it from the Hub Mods tab. Every cube is removed when the mod is disabled or unloaded.
]]

-- A complete falling-block game inside the STANDARD tier. It shows how far the plain Rbx API reaches:
-- a grid model in Lua tables, seven tetromino shapes with 90-degree rotation, line clears, input via
-- UserInputService (rising-edge for step moves, held S for soft-drop), and a fixed hooks_every loop
-- with a gravity accumulator. Rendering is a diff over a WIDTH x HEIGHT array of Parts (one cube per
-- filled cell, created/destroyed only on change) so the per-tick cost stays bounded. All cubes live
-- under one Folder the mod owns, so the runtime destroys the whole board on disable/unload.

local uis = game:GetService("UserInputService")

local WIDTH, HEIGHT = 6, 12
local ORIGIN = Vector3.new(-3, 1, 0)     -- world position of cell (1,1)
local GRAV_TICKS = 12                     -- normal gravity: one drop per this many ticks (~0.6s)
local SOFT_TICKS = 2                      -- soft-drop gravity while S is held

local PALETTE = {
    Color3.fromRGB(0, 200, 220),   -- I
    Color3.fromRGB(230, 210, 0),   -- O
    Color3.fromRGB(170, 0, 210),   -- T
    Color3.fromRGB(240, 140, 0),   -- L
    Color3.fromRGB(0, 90, 230),    -- J
    Color3.fromRGB(0, 200, 60),    -- S
    Color3.fromRGB(230, 40, 40),   -- Z
}

-- Base cell offsets (dx,dy) around each piece's pivot, rotation state 0.
local SHAPES = {
    { {-1, 0}, {0, 0}, {1, 0}, {2, 0} },   -- I
    { {0, 0}, {1, 0}, {0, 1}, {1, 1} },    -- O
    { {-1, 0}, {0, 0}, {1, 0}, {0, 1} },   -- T
    { {-1, 0}, {0, 0}, {1, 0}, {1, 1} },   -- L
    { {-1, 0}, {0, 0}, {1, 0}, {-1, 1} },  -- J
    { {0, 0}, {1, 0}, {-1, 1}, {0, 1} },   -- S
    { {-1, 0}, {0, 0}, {0, 1}, {1, 1} },   -- Z
}

local root = Instance.new("Folder")
root.Name = "Tetris3D"
root.Parent = workspace

-- Fixed angled camera framing the whole well. Unlike CameraSubject (which only follows a part's
-- position), a scripted CFrame controls the camera's position AND angle, exactly like Roblox: set
-- CameraType to Scriptable, then aim with CFrame.lookAt(eye, target). The board centre in cell space
-- is roughly (WIDTH/2, HEIGHT/2).
local BOARD_CENTER = Vector3.new(ORIGIN.X + (WIDTH / 2) - 0.5, ORIGIN.Y + (HEIGHT / 2) - 0.5, ORIGIN.Z)
local cam = workspace.CurrentCamera
cam.CameraType = Enum.CameraType.Scriptable
cam.CFrame = CFrame.lookAt(BOARD_CENTER + Vector3.new(0, 2, -18), BOARD_CENTER)

-- Static well frame (floor + left/right walls) so the play area reads clearly. These live under the
-- same Folder the mod owns, are built once, and are never touched by the diff-renderer below.
local WALL_COLOR = Color3.fromRGB(70, 70, 85)
local function wall_at(cx, cy)
    local w = Instance.new("Part")
    w.Name = "Wall"
    w.Size = Vector3.new(0.98, 0.98, 0.98)
    w.Color = WALL_COLOR
    w.Position = Vector3.new(ORIGIN.X + (cx - 1), ORIGIN.Y + (cy - 1), ORIGIN.Z)
    w.Parent = root
end
for x = 0, WIDTH + 1 do
    wall_at(x, 0)
end
for y = 1, HEIGHT do
    wall_at(0, y)
    wall_at(WIDTH + 1, y)
end

-- grid[y][x] = palette index (1..7) or nil; y=1 is the floor row.
local grid = {}
for y = 1, HEIGHT do
    grid[y] = {}
end

local partAt = {}                          -- partAt[y][x] = Part (diff-render cache)
for y = 1, HEIGHT do
    partAt[y] = {}
end

local piece                                -- { x, y, rot, kind }
local gravAccum = 0
local gameOver = false
local prevLeft, prevRight, prevRot = false, false, false

local function rot90(dx, dy)
    return dy, -dx
end

local function piece_cells(pc)
    local cells = {}
    for _, off in ipairs(SHAPES[pc.kind]) do
        local dx, dy = off[1], off[2]
        for _ = 1, pc.rot do
            dx, dy = rot90(dx, dy)
        end
        cells[#cells + 1] = { x = pc.x + dx, y = pc.y + dy }
    end
    return cells
end

local function collides(cells)
    for _, c in ipairs(cells) do
        if c.x < 1 or c.x > WIDTH or c.y < 1 then
            return true
        end
        if c.y <= HEIGHT and grid[c.y][c.x] then
            return true
        end
    end
    return false
end

local function occupied(x, y)
    if grid[y][x] then
        return grid[y][x]
    end
    if piece then
        for _, c in ipairs(piece_cells(piece)) do
            if c.x == x and c.y == y then
                return piece.kind
            end
        end
    end
    return nil
end

local function render()
    for y = 1, HEIGHT do
        for x = 1, WIDTH do
            local ci = occupied(x, y)
            local p = partAt[y][x]
            if ci then
                if not p then
                    p = Instance.new("Part")
                    p.Name = "Cell"
                    p.Size = Vector3.new(0.9, 0.9, 0.9)
                    p.Position = Vector3.new(ORIGIN.X + (x - 1), ORIGIN.Y + (y - 1), ORIGIN.Z)
                    p.Parent = root
                    partAt[y][x] = p
                end
                p.Color = PALETTE[ci]
            elseif p then
                p:Destroy()
                partAt[y][x] = nil
            end
        end
    end
end

local function spawn_piece()
    piece = { x = 3, y = HEIGHT, rot = 0, kind = math.random(1, 7) }
    if collides(piece_cells(piece)) then
        gameOver = true
        report("[tetris3d] GAME OVER. Disable/enable the mod to play again.")
    end
end

local function lock_piece()
    for _, c in ipairs(piece_cells(piece)) do
        if c.y >= 1 and c.y <= HEIGHT then
            grid[c.y][c.x] = piece.kind
        end
    end
    -- Clear full rows, top-down, compacting everything above the cleared row down by one.
    local cleared = 0
    local y = 1
    while y <= HEIGHT do
        local full = true
        for x = 1, WIDTH do
            if not grid[y][x] then
                full = false
                break
            end
        end
        if full then
            cleared = cleared + 1
            for yy = y, HEIGHT - 1 do
                for x = 1, WIDTH do
                    grid[yy][x] = grid[yy + 1][x]
                end
            end
            for x = 1, WIDTH do
                grid[HEIGHT][x] = nil
            end
        else
            y = y + 1
        end
    end
    if cleared > 0 then
        report("[tetris3d] cleared " .. cleared .. " row(s)")
    end
end

local function try_move(ddx, ddy, drot)
    local test = { x = piece.x + ddx, y = piece.y + ddy, rot = (piece.rot + drot) % 4, kind = piece.kind }
    if collides(piece_cells(test)) then
        return false
    end
    piece = test
    return true
end

spawn_piece()
render()
report("[tetris3d] loaded - A/D move, W rotate, S soft-drop. Fill rows to clear them.")

hooks_every(0.05, function()
    if gameOver then
        return
    end

    local left = uis:IsKeyDown(Enum.KeyCode.A) or uis:IsKeyDown(Enum.KeyCode.Left)
    local right = uis:IsKeyDown(Enum.KeyCode.D) or uis:IsKeyDown(Enum.KeyCode.Right)
    local rotate = uis:IsKeyDown(Enum.KeyCode.W) or uis:IsKeyDown(Enum.KeyCode.Up)
    local soft = uis:IsKeyDown(Enum.KeyCode.S) or uis:IsKeyDown(Enum.KeyCode.Down)

    if left and not prevLeft then try_move(-1, 0, 0) end
    if right and not prevRight then try_move(1, 0, 0) end
    if rotate and not prevRot then try_move(0, 0, 1) end
    prevLeft, prevRight, prevRot = left, right, rotate

    gravAccum = gravAccum + 1
    if gravAccum >= (soft and SOFT_TICKS or GRAV_TICKS) then
        gravAccum = 0
        if not try_move(0, -1, 0) then
            lock_piece()
            spawn_piece()
        end
    end

    render()
end)
