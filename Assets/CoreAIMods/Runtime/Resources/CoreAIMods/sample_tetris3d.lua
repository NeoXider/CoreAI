--[[@coreai
id: sample_tetris3d
name: Tetris 3D (sample)
version: 3.3.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Opt-in playable sample. A polished falling-block puzzle in 3D cubes - A/D move, W rotate, S soft-drop, R/Space restart. The active piece slides smoothly, new pieces drop in from above, and cleared rows burst apart. Written in pure Roblox API (Instance.new, Vector3, Color3, CFrame, UserInputService, RunService.Heartbeat, print) so it imports/exports 1:1. Ships disabled; enable it from the Hub Mods tab. Every cube is removed when the mod is disabled or unloaded.
]]

-- A polished falling-block game in the SAME API Roblox uses. Logic is a plain integer grid, but the
-- VISUALS are smoothed by hand (no engine tween needed): the active piece is four persistent cubes
-- eased toward their target cells each frame (so moves/rotations slide instead of teleporting) and a
-- new piece starts lifted above the well so it visibly drops in. Cleared rows and a game over become
-- "debris" cubes flung with a random velocity and arced under a hand-rolled gravity in Heartbeat, then
-- faded out - a burst effect without any physics API. The camera is framed first, then its RightVector
-- and LookVector are projected onto the ground once. Integer grid logic stays camera-independent;
-- only world positions are built from those axes, so A/D remain screen-horizontal after reframing.
-- Loop is RunService.Heartbeat(dt); everything is parented under one Folder the mod owns, so
-- disable/unload destroys it all.

local RunService = game:GetService("RunService")
local uis = game:GetService("UserInputService")

local WIDTH, HEIGHT = 6, 12
local GRAV_INTERVAL = 0.6                  -- seconds between gravity steps
local SOFT_INTERVAL = 0.05                 -- seconds between steps while S is held
local MAX_GRAVITY_STEPS_PER_FRAME = 8
local PIECE_EASE = 16                      -- how fast the active piece eases to its cell (higher=snappier)
local SPAWN_LIFT = 3                       -- studs a new piece starts above its cell (visible drop-in)
local DEBRIS_GRAVITY = 26                  -- studs/s^2 pulling burst cubes down
local DEBRIS_LIFE = 1.1                    -- seconds a burst cube lives before it is removed

local BOARD_CENTER = Vector3.new(-0.5, 6.5, 0)
local cam = workspace.CurrentCamera
cam.CameraType = Enum.CameraType.Scriptable
cam.CFrame = CFrame.lookAt(BOARD_CENTER + Vector3.new(0, 2, 18), BOARD_CENTER)

local cameraRight = cam.CFrame.RightVector
local cameraForward = cam.CFrame.LookVector
local HORIZONTAL_AXIS = Vector3.new(cameraRight.X, 0, cameraRight.Z).Unit
local DEPTH_AXIS = Vector3.new(cameraForward.X, 0, cameraForward.Z).Unit
local UP_AXIS = Vector3.new(0, 1, 0)

local function world_offset(horizontal, vertical, depth)
    return HORIZONTAL_AXIS * horizontal + UP_AXIS * vertical + DEPTH_AXIS * depth
end

local PALETTE = {
    Color3.fromRGB(0, 200, 220),   -- I
    Color3.fromRGB(230, 210, 0),   -- O
    Color3.fromRGB(170, 0, 210),   -- T
    Color3.fromRGB(240, 140, 0),   -- L
    Color3.fromRGB(0, 90, 230),    -- J
    Color3.fromRGB(0, 200, 60),    -- S
    Color3.fromRGB(230, 40, 40),   -- Z
}

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

local function cell_pos(x, y)
    local horizontal = (x - 1) - ((WIDTH - 1) / 2)
    local vertical = (y - 1) - ((HEIGHT - 1) / 2)
    return BOARD_CENTER + world_offset(horizontal, vertical, 0)
end

-- Static well frame (floor + walls), built once.
local WALL_COLOR = Color3.fromRGB(70, 70, 85)
local function wall_at(cx, cy)
    local w = Instance.new("Part")
    w.Name = "Wall"
    w.Size = Vector3.new(0.98, 0.98, 0.98)
    w.Color = WALL_COLOR
    w.Position = cell_pos(cx, cy)
    w.Anchored = true
    w.Parent = root
end
for x = 0, WIDTH + 1 do wall_at(x, 0) end
for y = 1, HEIGHT do wall_at(0, y) wall_at(WIDTH + 1, y) end

local grid = {}
for y = 1, HEIGHT do grid[y] = {} end
local lockedParts = {}                     -- ["x,y"] -> anchored cube for a settled cell
local pieceParts = {}                      -- 1..4 persistent cubes for the active piece (eased)
local debris = {}                          -- { part, sideSpeed, upSpeed, depthSpeed, life }

local piece
local gravAccum = 0
local activeGravityInterval = GRAV_INTERVAL
local gameOver = false
local prevLeft, prevRight, prevRot, prevRestart = false, false, false, false

local function rot90(dx, dy) return dy, -dx end

local function piece_cell(kind, pieceX, pieceY, rotation, index)
    local off = SHAPES[kind][index]
    local dx, dy = off[1], off[2]
    for _ = 1, rotation do dx, dy = rot90(dx, dy) end
    return pieceX + dx, pieceY + dy
end

local function collides(kind, pieceX, pieceY, rotation)
    for i = 1, 4 do
        local x, y = piece_cell(kind, pieceX, pieceY, rotation, i)
        if x < 1 or x > WIDTH or y < 1 then return true end
        if y <= HEIGHT and grid[y][x] then return true end
    end
    return false
end

-- Diff-render the settled grid into anchored cubes (these snap; only the active piece eases).
local function render_locked()
    local want = {}
    for y = 1, HEIGHT do
        for x = 1, WIDTH do
            if grid[y][x] then want[x .. "," .. y] = grid[y][x] end
        end
    end
    for key, part in pairs(lockedParts) do
        if not want[key] then part:Destroy() lockedParts[key] = nil end
    end
    for key, ci in pairs(want) do
        local p = lockedParts[key]
        if not p then
            local sx, sy = key:match("(%d+),(%d+)")
            p = Instance.new("Part")
            p.Name = "Cell"
            p.Size = Vector3.new(0.9, 0.9, 0.9)
            p.Position = cell_pos(tonumber(sx), tonumber(sy))
            p.Anchored = true
            p.Parent = root
            lockedParts[key] = p
        end
        p.Color = PALETTE[ci]
    end
end

local function make_debris(fromPart, color)
    local d = Instance.new("Part")
    d.Name = "Debris"
    d.Size = Vector3.new(0.9, 0.9, 0.9)
    d.Color = color
    d.Position = fromPart.Position
    d.Anchored = true
    d.Parent = root
    debris[#debris + 1] = {
        part = d,
        sideSpeed = (math.random() - 0.5) * 14,
        upSpeed = 6 + math.random() * 8,
        depthSpeed = -((math.random() - 0.5) * 14),
        life = DEBRIS_LIFE,
    }
end

local function spawn_piece()
    piece = { x = 3, y = HEIGHT, rot = 0, kind = math.random(1, 7) }
    for i = 1, 4 do
        if not pieceParts[i] then
            local p = Instance.new("Part")
            p.Name = "Cell"
            p.Size = Vector3.new(0.9, 0.9, 0.9)
            p.Anchored = true
            p.Parent = root
            pieceParts[i] = p
        end
        pieceParts[i].Color = PALETTE[piece.kind]
    end
    -- Start the cubes lifted above their target cells so the piece visibly drops in.
    for i = 1, 4 do
        local x, y = piece_cell(piece.kind, piece.x, piece.y, piece.rot, i)
        pieceParts[i].Position = cell_pos(x, y) + world_offset(0, SPAWN_LIFT, 0)
    end
    if collides(piece.kind, piece.x, piece.y, piece.rot) then
        gameOver = true
        -- Fun on loss: turn the whole board into a burst.
        for _, part in pairs(lockedParts) do make_debris(part, part.Color) part:Destroy() end
        lockedParts = {}
        for i = 1, 4 do make_debris(pieceParts[i], pieceParts[i].Color) end
        print("[tetris3d] GAME OVER - the board bursts apart! Press R or Space to restart.")
    end
end

local function lock_piece()
    for i = 1, 4 do
        local x, y = piece_cell(piece.kind, piece.x, piece.y, piece.rot, i)
        if y >= 1 and y <= HEIGHT then grid[y][x] = piece.kind end
    end
    local cleared = 0
    local y = 1
    while y <= HEIGHT do
        local full = true
        for x = 1, WIDTH do
            if not grid[y][x] then full = false break end
        end
        if full then
            cleared = cleared + 1
            -- Burst the cleared row before compacting the stack down over it.
            for x = 1, WIDTH do
                local part = lockedParts[x .. "," .. y]
                if part then make_debris(part, part.Color) part:Destroy() lockedParts[x .. "," .. y] = nil end
            end
            for yy = y, HEIGHT - 1 do
                for x = 1, WIDTH do grid[yy][x] = grid[yy + 1][x] end
            end
            for x = 1, WIDTH do grid[HEIGHT][x] = nil end
        else
            y = y + 1
        end
    end
    render_locked()
    if cleared > 0 then print("[tetris3d] cleared " .. cleared .. " row(s)") end
end

local function reset_board()
    for y = 1, HEIGHT do for x = 1, WIDTH do grid[y][x] = nil end end
    for _, p in pairs(lockedParts) do p:Destroy() end
    lockedParts = {}
    for _, d in ipairs(debris) do d.part:Destroy() end
    debris = {}
    gravAccum = 0
    activeGravityInterval = GRAV_INTERVAL
    gameOver = false
    spawn_piece()
end

local function try_move(ddx, ddy, drot)
    local nextX = piece.x + ddx
    local nextY = piece.y + ddy
    local nextRotation = (piece.rot + drot) % 4
    if collides(piece.kind, nextX, nextY, nextRotation) then return false end
    piece.x = nextX
    piece.y = nextY
    piece.rot = nextRotation
    return true
end

local function animate_debris(dt)
    for i = #debris, 1, -1 do
        local d = debris[i]
        d.upSpeed = d.upSpeed - DEBRIS_GRAVITY * dt
        d.part.Position = d.part.Position + world_offset(d.sideSpeed, d.upSpeed, d.depthSpeed) * dt
        d.life = d.life - dt
        if d.life <= 0 then
            d.part:Destroy()
            table.remove(debris, i)
        end
    end
end

-- Ease each active-piece cube toward its logical cell so the piece slides smoothly.
local function ease_piece(dt)
    if not piece then return end
    local t = math.min(1, dt * PIECE_EASE)
    for i = 1, 4 do
        local p = pieceParts[i]
        local x, y = piece_cell(piece.kind, piece.x, piece.y, piece.rot, i)
        local target = cell_pos(x, y)
        p.Position = p.Position + (target - p.Position) * t
    end
end

spawn_piece()
render_locked()
print("[tetris3d] loaded - A/D move, W rotate, S soft-drop. Fill rows to clear them. R/Space restart.")

RunService.Heartbeat:Connect(function(dt)
    animate_debris(dt)

    if gameOver then
        local pressed = uis:IsKeyDown(Enum.KeyCode.R) or uis:IsKeyDown(Enum.KeyCode.Space)
        if pressed and not prevRestart then
            reset_board()
            render_locked()
            print("[tetris3d] restarted - go!")
        end
        prevRestart = pressed
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

    local nextGravityInterval = soft and SOFT_INTERVAL or GRAV_INTERVAL
    if nextGravityInterval ~= activeGravityInterval then
        gravAccum = gravAccum / activeGravityInterval * nextGravityInterval
        activeGravityInterval = nextGravityInterval
    end
    gravAccum = gravAccum + dt
    local gravitySteps = 0
    while not gameOver and gravAccum >= activeGravityInterval and gravitySteps < MAX_GRAVITY_STEPS_PER_FRAME do
        gravAccum = gravAccum - activeGravityInterval
        gravitySteps = gravitySteps + 1
        if not try_move(0, -1, 0) then
            lock_piece()
            spawn_piece()
        end
    end
    -- A long stall (alt-tab, a breakpoint) can bank hundreds of intervals. The cap above keeps any one
    -- frame cheap; dropping the surplus here stops the piece slamming down over the frames that follow.
    if gravitySteps >= MAX_GRAVITY_STEPS_PER_FRAME then
        gravAccum = gravAccum % activeGravityInterval
    end

    ease_piece(dt)
end)
