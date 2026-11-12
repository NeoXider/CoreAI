--[[@coreai
id: blockworld_graphics
name: Block World 10 - Graphics
version: 1.1.0
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo. O opens the graphics settings, a panel of Roblox GUI objects (ScreenGui, Frame, TextLabel, TextButton) with three settings: the view distance, how many columns of 16 blocks the world streams around you (2 up to the host's limit: 16 on a desktop; the fog follows it, and a longer one fills in as it streams), the field of view (Camera.FieldOfView, 50 to 110 degrees) and the sun's brightness (Lighting.Brightness, 50 to 200 percent). Each choice is a workspace attribute (BlockWorldViewRadius, BlockWorldFieldOfView, BlockWorldBrightness), so a world kept between starts opens with the same settings, and a script or the AI can change one the same way. The panel frees the mouse while it is open; O again, or Done, closes it. While you sprint (BlockWorldSprinting, from blockworld_build) the field of view widens by 15 percent over your choice and narrows back when the sprint ends, as in Minecraft.
]]

-- The settings are workspace attributes because a world kept between starts keeps the Workspace's attributes, and
-- neither a mod's instances nor the camera's lens: blockworld_world follows BlockWorldViewRadius itself, and this mod
-- applies the other two to Camera.FieldOfView and Lighting.Brightness when it loads and whenever they change. A choice
-- made in the panel also raises CoreAIWorldEdits, which tells a kept world to record itself, as a placed block does.
--
-- Reads: BlockWorldStreamRadius and BlockWorldViewRadiusLimit (blockworld_world), BlockWorldSprinting
-- (blockworld_build). Writes: BlockWorldViewRadius, BlockWorldFieldOfView, BlockWorldBrightness, CoreAIWorldEdits.

local Lighting = game:GetService("Lighting")
local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")

local TOGGLE_KEY = Enum.KeyCode.O
local PANEL_WIDTH = 520
local PANEL_HEIGHT = 352
local ROW_HEIGHT = 44
local SPRINT_FIELD = 1.15       -- the field of view while sprinting, as a part of the chosen one
local SPRINT_EASE = 10          -- how fast the widening comes and goes, per second: most of it in a quarter second

local function number(name)
    local value = workspace:GetAttribute(name)
    if type(value) == "number" and value == value then
        return value
    end
    return nil
end

-- ---- applying a setting ---------------------------------------------------------------------------------------

-- The lens is the chosen field of view times the sprint's widening (lensFactor, 1 while walking). WHY one owner: the
-- setting and the sprint both move Camera.FieldOfView, and two mods writing it would undo each other.
local lensFactor = 1
local easing = nil               -- the Heartbeat connection that moves the lens, while it moves
local restingFieldOfView = nil   -- the camera's own field of view when a sprint began, with no setting chosen

local function chosenFieldOfView()
    local value = number("BlockWorldFieldOfView")
    if value then
        return value
    end
    if easing == nil and lensFactor == 1 then
        local camera = workspace.CurrentCamera
        return camera and camera.FieldOfView or 70
    end
    return restingFieldOfView or 70
end

local function writeLens()
    local camera = workspace.CurrentCamera
    if not camera then
        return
    end
    local wanted = chosenFieldOfView() * lensFactor
    local ok, problem = pcall(function()
        camera.FieldOfView = wanted
    end)
    if not ok then
        warn("[blockworld] the field of view stays as it is: " .. tostring(problem))
    end
end

local function applyFieldOfView()
    if number("BlockWorldFieldOfView") or lensFactor ~= 1 then
        writeLens()
    end
end

local function applyBrightness()
    local value = number("BlockWorldBrightness")
    if value then
        Lighting.Brightness = math.max(value, 0)
    end
end

-- The rows of the panel, top to bottom: the attribute a choice goes to, its range and step, the value in use now, how
-- it reads and how it is applied at once (WHY: the attribute's signal runs a frame later, and a second click in
-- between would repeat the first). WHY the view distance is the asked one, held to the host's limit as the world mod
-- holds it, and not the world mod's BlockWorldStreamRadius: that too follows a frame later. With nothing asked it is
-- the radius the world mod streams (8, its default, until it says).
local SETTINGS = {
    {
        name = "ViewDistance", label = "View distance", attribute = "BlockWorldViewRadius", step = 1, min = 2,
        max = function()
            return math.max(2, number("BlockWorldViewRadiusLimit") or 16)
        end,
        current = function()
            local wanted = number("BlockWorldViewRadius")
            if wanted then
                return math.clamp(math.floor(wanted), 1, math.max(2, number("BlockWorldViewRadiusLimit") or 16))
            end
            return number("BlockWorldStreamRadius") or 8
        end,
        describe = function(value)
            return value .. " columns, " .. value * 16 .. " blocks"
        end,
    },
    {
        name = "FieldOfView", label = "Field of view", attribute = "BlockWorldFieldOfView", step = 5, min = 50,
        max = function()
            return 110
        end,
        current = function()
            return math.floor(chosenFieldOfView() + 0.5)
        end,
        describe = function(value)
            return value .. " degrees"
        end,
        apply = applyFieldOfView,
    },
    {
        name = "SunBrightness", label = "Sun brightness", attribute = "BlockWorldBrightness", step = 0.25, min = 0.5,
        max = function()
            return 2
        end,
        current = function()
            return Lighting.Brightness
        end,
        describe = function(value)
            return math.floor(value * 100 + 0.5) .. " percent"
        end,
        apply = applyBrightness,
    },
}

-- ---- the panel --------------------------------------------------------------------------------------------------

local function newLabel(parent, name, text, position, size, alignment, font, textSize)
    local label = Instance.new("TextLabel")
    label.Name = name
    label.Position = position
    label.Size = size
    label.BackgroundTransparency = 1
    label.Text = text
    label.TextColor3 = Color3.fromRGB(240, 240, 240)
    label.TextSize = textSize or 18
    label.Font = font or Enum.Font.Gotham
    label.TextXAlignment = alignment or Enum.TextXAlignment.Left
    label.Parent = parent
    return label
end

local function newButton(parent, name, text, position, size)
    local button = Instance.new("TextButton")
    button.Name = name
    button.Position = position
    button.Size = size
    button.BackgroundColor3 = Color3.fromRGB(52, 60, 78)
    button.BorderSizePixel = 0
    button.Text = text
    button.TextColor3 = Color3.fromRGB(255, 255, 255)
    button.TextSize = 22
    button.Font = Enum.Font.GothamBold
    button.Parent = parent
    local corner = Instance.new("UICorner")
    corner.CornerRadius = UDim.new(0, 6)
    corner.Parent = button
    return button
end

local gui = Instance.new("ScreenGui")
gui.Name = "BlockWorldGraphics"
gui.ResetOnSpawn = false
gui.IgnoreGuiInset = true
gui.DisplayOrder = 20
gui.Enabled = false

-- WHY a backdrop over the whole screen that takes the pointer: while the pointer rests on a GUI object the default
-- camera leaves it free and the view still, so a click lands on the panel and never breaks a block behind it.
local backdrop = Instance.new("Frame")
backdrop.Name = "Backdrop"
backdrop.Size = UDim2.fromScale(1, 1)
backdrop.BackgroundColor3 = Color3.fromRGB(8, 10, 16)
backdrop.BackgroundTransparency = 0.45
backdrop.BorderSizePixel = 0
backdrop.Active = true
backdrop.Parent = gui

local panel = Instance.new("Frame")
panel.Name = "Panel"
panel.AnchorPoint = Vector2.new(0.5, 0.5)
panel.Position = UDim2.fromScale(0.5, 0.5)
panel.Size = UDim2.fromOffset(PANEL_WIDTH, PANEL_HEIGHT)
panel.BackgroundColor3 = Color3.fromRGB(18, 22, 32)
panel.BackgroundTransparency = 0.05
panel.BorderSizePixel = 0
panel.Parent = backdrop
local panelCorner = Instance.new("UICorner")
panelCorner.CornerRadius = UDim.new(0, 10)
panelCorner.Parent = panel
local padding = Instance.new("UIPadding")
padding.PaddingLeft = UDim.new(0, 20)
padding.PaddingRight = UDim.new(0, 20)
padding.PaddingTop = UDim.new(0, 16)
padding.PaddingBottom = UDim.new(0, 16)
padding.Parent = panel
local layout = Instance.new("UIListLayout")
layout.FillDirection = Enum.FillDirection.Vertical
layout.SortOrder = Enum.SortOrder.LayoutOrder
layout.Padding = UDim.new(0, 10)
layout.Parent = panel

local title = newLabel(panel, "Title", "Graphics", UDim2.new(), UDim2.new(1, 0, 0, 36), Enum.TextXAlignment.Left,
    Enum.Font.GothamBold, 26)
title.LayoutOrder = 1

local rows = {}
for index, setting in ipairs(SETTINGS) do
    local row = Instance.new("Frame")
    row.Name = setting.name
    row.LayoutOrder = index + 1
    row.Size = UDim2.new(1, 0, 0, ROW_HEIGHT)
    row.BackgroundTransparency = 1
    row.Parent = panel
    newLabel(row, "Name", setting.label, UDim2.new(), UDim2.fromOffset(170, ROW_HEIGHT))
    rows[index] = {
        setting = setting,
        less = newButton(row, "Less", "-", UDim2.fromOffset(180, 4), UDim2.fromOffset(36, 36)),
        value = newLabel(row, "Value", "", UDim2.fromOffset(222, 0), UDim2.fromOffset(212, ROW_HEIGHT),
            Enum.TextXAlignment.Center, Enum.Font.GothamBold),
        more = newButton(row, "More", "+", UDim2.fromOffset(440, 4), UDim2.fromOffset(36, 36)),
    }
end

local hint = newLabel(panel, "Hint", "O closes this panel. A longer view fills in as the land streams.", UDim2.new(),
    UDim2.new(1, 0, 0, 28), Enum.TextXAlignment.Left, Enum.Font.Gotham, 15)
hint.TextColor3 = Color3.fromRGB(190, 196, 210)
hint.LayoutOrder = #SETTINGS + 2

local done = newButton(panel, "Done", "Done", UDim2.new(), UDim2.new(1, 0, 0, 40))
done.LayoutOrder = #SETTINGS + 3
-- WHY Modal: it is Roblox's own way for a GUI to free the mouse in first person; this host records it, and setOpen
-- frees the mouse itself.
done.Modal = true

local function refresh()
    for _, row in ipairs(rows) do
        local setting = row.setting
        local now = setting.current()
        row.value.Text = setting.describe(now)
        row.less.TextTransparency = now <= setting.min and 0.65 or 0
        row.more.TextTransparency = now >= setting.max() and 0.65 or 0
    end
end

-- The next value on the setting's grid in `direction`, held to its range; a value off the grid (one the AI set) goes
-- to the grid's next mark.
local function change(setting, direction)
    local now = setting.current()
    local step = setting.step
    local value
    if direction > 0 then
        value = (math.floor(now / step + 1e-6) + 1) * step
    else
        value = (math.ceil(now / step - 1e-6) - 1) * step
    end
    value = math.clamp(value, setting.min, setting.max())
    if value == now then
        return
    end
    workspace:SetAttribute(setting.attribute, value)
    workspace:SetAttribute("CoreAIWorldEdits", (workspace:GetAttribute("CoreAIWorldEdits") or 0) + 1)
    if setting.apply then
        setting.apply()
    end
    refresh()
end

for _, row in ipairs(rows) do
    row.less.MouseButton1Click:Connect(function()
        change(row.setting, -1)
    end)
    row.more.MouseButton1Click:Connect(function()
        change(row.setting, 1)
    end)
end

-- ---- opening and closing ------------------------------------------------------------------------------------

local function attach()
    local player = Players.LocalPlayer or Players:GetPlayers()[1]
    local playerGui = player and player:FindFirstChildOfClass("PlayerGui")
    if playerGui and gui.Parent ~= playerGui then
        gui.Parent = playerGui
    end
end

local open = false
local function setOpen(value)
    open = value
    attach()
    gui.Enabled = value
    if value then
        refresh()
        -- WHY: in first person the camera holds the pointer at the centre of the view; it lets go while the pointer
        -- rests on the panel, but the pointer has to leave the lock to get there.
        UserInputService.MouseBehavior = Enum.MouseBehavior.Default
    end
end

done.MouseButton1Click:Connect(function()
    setOpen(false)
end)

UserInputService.InputBegan:Connect(function(input, gameProcessed)
    if gameProcessed or input.KeyCode ~= TOGGLE_KEY then
        return
    end
    setOpen(not open)
end)

workspace:GetAttributeChangedSignal("BlockWorldFieldOfView"):Connect(function()
    applyFieldOfView()
    refresh()
end)
workspace:GetAttributeChangedSignal("BlockWorldBrightness"):Connect(function()
    applyBrightness()
    refresh()
end)
for _, name in ipairs({ "BlockWorldViewRadius", "BlockWorldStreamRadius", "BlockWorldViewRadiusLimit" }) do
    workspace:GetAttributeChangedSignal(name):Connect(refresh)
end
workspace:GetPropertyChangedSignal("CurrentCamera"):Connect(applyFieldOfView)

-- The sprint's widening, eased in and out; the Heartbeat handler is connected only while the lens moves.
local lensTarget = 1
local function followSprint()
    if easing == nil and lensFactor == 1 then
        restingFieldOfView = chosenFieldOfView()
    end
    lensTarget = workspace:GetAttribute("BlockWorldSprinting") == true and SPRINT_FIELD or 1
    if easing or lensFactor == lensTarget then
        return
    end
    easing = RunService.Heartbeat:Connect(function(dt)
        lensFactor = lensFactor + (lensTarget - lensFactor) * math.min(1, dt * SPRINT_EASE)
        local settled = math.abs(lensTarget - lensFactor) < 0.002
        if settled then
            lensFactor = lensTarget
        end
        writeLens()
        if settled then
            easing:Disconnect()
            easing = nil
        end
    end)
end
workspace:GetAttributeChangedSignal("BlockWorldSprinting"):Connect(followSprint)
Players.PlayerAdded:Connect(attach)

attach()
applyFieldOfView()
applyBrightness()
refresh()
