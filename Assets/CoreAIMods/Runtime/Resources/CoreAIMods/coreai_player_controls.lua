--[[@coreai
id: coreai_player_controls
name: Player Controls (system)
version: 1.0.0
active: true
capabilities: All
category: System
author: CoreAI
description: Default character controls, the CoreAI counterpart of Roblox's PlayerModule ControlModule. WASD or the arrow keys (or the gamepad left thumbstick or D-pad) walk relative to the camera, Space (or gamepad A) jumps, and a double-tapped jump toggles free flight, where Space rises and Left Shift descends. Input the engine consumed (the chat or the Hub has focus) is ignored. Turn it off with StarterPlayer.DevComputerMovementMode = Enum.DevComputerMovementMode.Scriptable, or unload this mod and ship your own. Written as the client-side script it becomes once script contexts land.
]]

local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")
local StarterPlayer = game:GetService("StarterPlayer")

local DOUBLE_TAP_SECONDS = 0.3
-- WHY 0.2: the PlayerModule's gamepad dead zone; a resting stick reads a little off centre.
local THUMBSTICK_DEADZONE = 0.2

local MOVE_KEYS = {
	W = Vector3.new(0, 0, -1),
	Up = Vector3.new(0, 0, -1),
	DPadUp = Vector3.new(0, 0, -1),
	S = Vector3.new(0, 0, 1),
	Down = Vector3.new(0, 0, 1),
	DPadDown = Vector3.new(0, 0, 1),
	A = Vector3.new(-1, 0, 0),
	Left = Vector3.new(-1, 0, 0),
	DPadLeft = Vector3.new(-1, 0, 0),
	D = Vector3.new(1, 0, 0),
	Right = Vector3.new(1, 0, 0),
	DPadRight = Vector3.new(1, 0, 0),
}

local JUMP_KEYS = {
	Space = true,
	ButtonA = true,
}

local held = {}
local stick = Vector3.new(0, 0, 0)
local lastJumpPress = -math.huge
local wasControlling = false

local function getHumanoid()
	-- WHY the fallback: until script contexts land, a solo world may run this as a server-side mod,
	-- where LocalPlayer is nil and the one connected player is the person at this screen.
	local player = Players.LocalPlayer or Players:GetPlayers()[1]
	local character = player and player.Character
	if not character then
		return nil
	end
	return character:FindFirstChildOfClass("Humanoid")
end

local function controlsEnabled()
	local mode = StarterPlayer.DevComputerMovementMode
	return mode ~= Enum.DevComputerMovementMode.Scriptable
		and mode ~= Enum.DevComputerMovementMode.ClickToMove
end

local function isFlying(humanoid)
	return humanoid:GetState() == Enum.HumanoidStateType.Flying
end

local function onJumpPressed(humanoid)
	local now = time()
	if now - lastJumpPress <= DOUBLE_TAP_SECONDS then
		lastJumpPress = -math.huge
		if isFlying(humanoid) then
			humanoid:ChangeState(Enum.HumanoidStateType.Freefall)
		else
			pcall(humanoid.ChangeState, humanoid, Enum.HumanoidStateType.Flying)
		end
		return
	end
	lastJumpPress = now
	if not isFlying(humanoid) then
		humanoid.Jump = true
	end
end

UserInputService.InputBegan:Connect(function(input, gameProcessed)
	if gameProcessed or not controlsEnabled() then
		return
	end
	held[input.KeyCode.Name] = true
	if JUMP_KEYS[input.KeyCode.Name] then
		local humanoid = getHumanoid()
		if humanoid then
			onJumpPressed(humanoid)
		end
	end
end)

UserInputService.InputEnded:Connect(function(input)
	held[input.KeyCode.Name] = nil
end)

UserInputService.InputChanged:Connect(function(input, gameProcessed)
	if input.KeyCode ~= Enum.KeyCode.Thumbstick1 then
		return
	end
	local x, y = input.Position.X, input.Position.Y
	if gameProcessed or not controlsEnabled() or Vector2.new(x, y).Magnitude < THUMBSTICK_DEADZONE then
		stick = Vector3.new(0, 0, 0)
	else
		-- WHY -y: the stick's y is up and pushing it forward walks toward the camera's -Z.
		stick = Vector3.new(x, 0, -y)
	end
end)

RunService.Heartbeat:Connect(function()
	local humanoid = getHumanoid()
	if not humanoid then
		return
	end

	if not controlsEnabled() then
		if wasControlling then
			wasControlling = false
			held = {}
			stick = Vector3.new(0, 0, 0)
			humanoid:Move(Vector3.new(0, 0, 0))
		end
		return
	end

	local direction = stick
	for keyName, vector in pairs(MOVE_KEYS) do
		if held[keyName] then
			direction = direction + vector
		end
	end

	if isFlying(humanoid) then
		local vertical = 0
		if held.Space or held.ButtonA then
			vertical = vertical + 1
		end
		if held.LeftShift then
			vertical = vertical - 1
		end
		direction = direction + Vector3.new(0, vertical, 0)
	end

	if direction.Magnitude > 0 or wasControlling then
		humanoid:Move(direction, true)
	end
	wasControlling = direction.Magnitude > 0
end)
