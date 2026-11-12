--[[@coreai
id: coreai_chat
name: Chat (system)
version: 1.0.0
context: client
active: false
capabilities: All
category: System
author: CoreAI
description: The in-world chat made in Lua, the counterpart of Roblox's chat CoreScripts. T or Enter opens a one-line input (the slash key opens it with / typed), Enter sends and closes, Enter on an empty line only closes, Escape closes without sending. A line goes to the villagers first (through TextChannel:SendAsync) and then to the player's own AI agent (AIService:GetAssistant), whose answer streams into the feed while a status line shows thinking, writing a script, running a tool, answering or retrying with the elapsed time. /help, /agent, /model and /stop are TextChatCommands of this mod; the game's own commands (/undo, /fly, /autopilot ...) run through the same chat. Lines fade after ten seconds and come back while the input is open. Turning this mod on switches CoreAI's built-in chat off (ChatInputBarConfiguration.Enabled = false) for as long as it runs. It needs the host setting CoreAiLuaAiOptions.AllowPlayerAssistantFromLua; without it the mod stays idle and the built-in chat keeps working. Every key, text, colour and size is in CONFIG at the top.
]]

local Players = game:GetService("Players")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")
local TextChatService = game:GetService("TextChatService")

local CONFIG = {
	OpenKeys = { T = true, Return = true, KeypadEnter = true },
	CommandKeys = { Slash = true, KeypadDivide = true },
	CloseKey = "Escape",
	Prefix = "/",
	FadeDelaySeconds = 10,
	MaxLines = 100,
	MaxInput = 512,
	Width = 620,
	FeedHeight = 300,
	InputHeight = 34,
	StatusHeight = 24,
	TextSize = 17,
	Margin = 16,
	LineGap = 4,
	LinePadding = 4,
	DisplayOrder = 90,
	PlayerName = "You",
	AssistantName = "AI",
	Texts = {
		Placeholder = "Say something or type /help",
		NotConnected = "The AI is not connected.",
		Busy = "The AI is still answering. Type /stop to cancel it.",
		Stopped = "Stopped.",
		NoAnswer = "The AI returned no answer.",
		ToolOnlyDone = "Done.",
		ToolOnlyFailed = "I could not do that. Ask again, or say it another way.",
		UnknownCommand = "Unknown command /%s. Type /help for the list.",
		EmptyCommand = "Type a command after /, or /help for the list.",
		NothingToStop = "Nothing to stop.",
		GameCommand = "A game command.",
	},
	Status = {
		Thinking = "thinking",
		Answering = "answering",
		RunningTool = "running %s",
		Retrying = "%s failed, retrying",
		ComposingTool = "preparing %s",
		RetryAttempt = " (retry %d)",
		AttemptOf = " (%d of %d)",
		Elapsed = " " .. utf8.char(183) .. " %ds",
		Tokens = " " .. utf8.char(183) .. " ~%s tokens",
		Step = " " .. utf8.char(183) .. " step %d",
		ShowAfterSeconds = 1,
		ToolTexts = { manage_mods = "loading mod" },
		ComposingTexts = { execute_lua = "writing a script", manage_mods = "writing a mod" },
	},
	Colors = {
		player = Color3.fromRGB(255, 255, 255),
		assistant = Color3.fromRGB(150, 215, 255),
		system = Color3.fromRGB(235, 225, 160),
		command = Color3.fromRGB(170, 240, 170),
		error = Color3.fromRGB(255, 140, 130),
		status = Color3.fromRGB(200, 200, 200),
		lineBackground = Color3.fromRGB(0, 0, 0),
		inputBackground = Color3.fromRGB(20, 20, 24),
	},
	LineBackgroundTransparency = 0.55,
}

local inputBar = TextChatService.ChatInputBarConfiguration
local chatWindow = TextChatService.ChatWindowConfiguration
local general = TextChatService.TextChannels.RBXGeneral

local okAssistant, assistant = pcall(function()
	return game:GetService("AIService"):GetAssistant()
end)
if not okAssistant then
	warn("[coreai_chat] The AI cannot be reached from Lua here, so the built-in chat stays on: " .. tostring(assistant))
	return
end

-- WHY first: the built-in chat gives its own /help, /agent, /model and /stop back the moment it steps aside, so
-- this mod's commands of those names can register right after.
inputBar.Enabled = false
chatWindow.Enabled = false

local player = Players.LocalPlayer or Players:GetPlayers()[1]
while not player do
	player = Players.PlayerAdded:Wait()
end

local playerGui = player:WaitForChild("PlayerGui")

-- ---- the interface --------------------------------------------------------------------------------------

local screen = Instance.new("ScreenGui")
screen.Name = "CoreAIChat"
screen.DisplayOrder = CONFIG.DisplayOrder
screen.ResetOnSpawn = false

local column = Instance.new("Frame")
column.Name = "Column"
column.AnchorPoint = Vector2.new(0, 1)
column.Position = UDim2.new(0, CONFIG.Margin, 1, -CONFIG.Margin)
column.Size = UDim2.fromOffset(CONFIG.Width, CONFIG.FeedHeight + CONFIG.InputHeight + CONFIG.StatusHeight + 8)
column.BackgroundTransparency = 1
column.Parent = screen

local feed = Instance.new("ScrollingFrame")
feed.Name = "Feed"
feed.Size = UDim2.fromOffset(CONFIG.Width, CONFIG.FeedHeight)
feed.BackgroundTransparency = 1
feed.CanvasSize = UDim2.new()
feed.AutomaticCanvasSize = Enum.AutomaticSize.Y
feed.ScrollingDirection = Enum.ScrollingDirection.Y
feed.ScrollBarThickness = 0
feed.Interactable = false
feed.Parent = column

local layout = Instance.new("UIListLayout")
layout.SortOrder = Enum.SortOrder.LayoutOrder
layout.VerticalAlignment = Enum.VerticalAlignment.Bottom
layout.Padding = UDim.new(0, CONFIG.LineGap)
layout.Parent = feed

local input = Instance.new("TextBox")
input.Name = "Input"
input.Position = UDim2.fromOffset(0, CONFIG.FeedHeight + 4)
input.Size = UDim2.fromOffset(CONFIG.Width, CONFIG.InputHeight)
input.BackgroundColor3 = CONFIG.Colors.inputBackground
input.BackgroundTransparency = 0.2
input.TextColor3 = CONFIG.Colors.player
input.TextSize = CONFIG.TextSize
input.TextXAlignment = Enum.TextXAlignment.Left
input.ClearTextOnFocus = false
input.PlaceholderText = CONFIG.Texts.Placeholder
input.Text = ""
input.Visible = false
input.Parent = column

local status = Instance.new("TextLabel")
status.Name = "Status"
status.Position = UDim2.fromOffset(0, CONFIG.FeedHeight + CONFIG.InputHeight + 8)
status.Size = UDim2.fromOffset(CONFIG.Width, CONFIG.StatusHeight)
status.BackgroundTransparency = 1
status.TextColor3 = CONFIG.Colors.status
status.TextSize = CONFIG.TextSize - 2
status.TextXAlignment = Enum.TextXAlignment.Left
status.Text = ""
status.Visible = false
status.Parent = column

screen.Parent = playerGui

-- ---- the feed -----------------------------------------------------------------------------------------

local lines = {}
local lineCounter = 0
local open = false
local stickToBottom = true

local function render(line)
	line.label.Text = line.speaker ~= "" and (line.speaker .. ": " .. line.text) or line.text
end

local function scrollToBottom()
	if stickToBottom then
		feed.CanvasPosition = Vector2.new(0, 1000000)
	end
end

local function setShown(line, shown)
	if line.shown == shown then
		return
	end
	line.shown = shown
	line.label.TextTransparency = shown and 0 or 1
	line.label.BackgroundTransparency = shown and CONFIG.LineBackgroundTransparency or 1
end

local function lineVisible(line, now)
	return open or line.streaming or now - line.changedAt < CONFIG.FadeDelaySeconds
end

local function addLine(kind, speaker, text, streaming)
	lineCounter = lineCounter + 1
	local label = Instance.new("TextLabel")
	label.Name = "Line" .. lineCounter
	label.LayoutOrder = lineCounter
	label.BackgroundColor3 = CONFIG.Colors.lineBackground
	label.BackgroundTransparency = CONFIG.LineBackgroundTransparency
	label.TextColor3 = CONFIG.Colors[kind] or CONFIG.Colors.system
	label.TextSize = CONFIG.TextSize
	label.TextWrapped = true
	label.TextXAlignment = Enum.TextXAlignment.Left
	label.TextYAlignment = Enum.TextYAlignment.Top
	-- The width is the feed's, the height grows to the wrapped text.
	label.Size = UDim2.new(1, -8, 0, 0)
	label.AutomaticSize = Enum.AutomaticSize.Y
	local padding = Instance.new("UIPadding")
	padding.PaddingLeft = UDim.new(0, CONFIG.LinePadding)
	padding.PaddingRight = UDim.new(0, CONFIG.LinePadding)
	padding.PaddingTop = UDim.new(0, CONFIG.LinePadding / 2)
	padding.PaddingBottom = UDim.new(0, CONFIG.LinePadding / 2)
	padding.Parent = label
	label:SetAttribute("Kind", kind)
	local line = {
		kind = kind,
		speaker = speaker or "",
		text = text or "",
		label = label,
		streaming = streaming == true,
		changedAt = os.clock(),
		shown = true,
	}
	render(line)
	label.Parent = feed
	table.insert(lines, line)
	while #lines > CONFIG.MaxLines do
		table.remove(lines, 1).label:Destroy()
	end
	scrollToBottom()
	return line
end

local function appendToLine(line, delta)
	line.text = line.text .. delta
	line.changedAt = os.clock()
	render(line)
	setShown(line, true)
	scrollToBottom()
end

local function refreshVisibility()
	local now = os.clock()
	for _, line in ipairs(lines) do
		setShown(line, lineVisible(line, now))
	end
end

-- ---- the agent's turn and the status line -------------------------------------------------------------

local current = nil

-- WHY to the nearest ten below a thousand: the count is an estimate, and more digits would only flicker (the C#
-- chat rounds the same way).
local function formatTokens(tokens)
	local rounded = math.max(10, math.floor(tokens / 10 + 0.5) * 10)
	if rounded >= 1000 then
		return string.format("%.1fk", tokens / 1000)
	end
	return tostring(rounded)
end

-- The status line follows the assistant as a whole, so turns the host runs (a mod's self-repair, an orchestrator
-- wake) and the sub-agents working in the background show here too, not only the turns this chat sent.
local liveStatus = assistant.Status
local statusStarted = os.clock()

local function statusText(s, started, now)
	local texts = CONFIG.Status
	if s.State == "Idle" then
		return s.Background or ""
	end
	local tool = s.Tool or ""
	local kind = s.State or "Thinking"
	local text
	if (kind == "Thinking" or kind == "RunningTool" or kind == "ComposingTool") and (s.Detail or "") ~= "" then
		text = s.Detail
	elseif kind == "ComposingTool" then
		text = texts.ComposingTexts[tool] or texts.ComposingTool:format(tool)
	elseif kind == "RunningTool" then
		text = texts.ToolTexts[tool] or texts.RunningTool:format(tool)
		if (s.Attempt or 0) > 0 then
			text = text .. texts.RetryAttempt:format(s.Attempt)
		end
	elseif kind == "Retrying" then
		text = texts.Retrying:format(tool)
		if (s.MaxAttempts or 0) > 0 then
			text = text .. texts.AttemptOf:format(math.max(1, s.Attempt or 1), s.MaxAttempts)
		end
	elseif kind == "Answering" then
		text = texts.Answering
	else
		text = texts.Thinking
	end
	local elapsed = now - started
	if elapsed >= texts.ShowAfterSeconds then
		text = text .. texts.Elapsed:format(math.floor(elapsed))
		if (s.Tokens or 0) > 0 then
			text = text .. texts.Tokens:format(formatTokens(s.Tokens))
		end
	end
	if (s.Step or 0) > 1 then
		text = text .. texts.Step:format(s.Step)
	end
	return text
end

local shownStatus = ""
local function refreshStatus()
	local text = liveStatus and statusText(liveStatus, statusStarted, os.clock()) or ""
	if text == shownStatus then
		return
	end
	shownStatus = text
	status.Text = text
	status.Visible = text ~= ""
end

local function assistantSpeaker()
	return CONFIG.AssistantName
end

assistant.StatusChanged:Connect(function(newStatus)
	if newStatus.State ~= "Idle" and (liveStatus == nil or liveStatus.State == "Idle") then
		statusStarted = os.clock()
	end
	liveStatus = newStatus
	refreshStatus()
end)

local function endTurn(state, result)
	if current ~= state then
		return
	end
	current = nil
	if state.line then
		state.line.streaming = false
		state.line.changedAt = os.clock()
	end
	local outcome = result.Outcome
	if outcome == "Cancelled" then
		addLine("system", "", CONFIG.Texts.Stopped)
	elseif outcome == "Failed" or outcome == "TimedOut" then
		addLine("error", "", result.Message ~= "" and result.Message or CONFIG.Texts.NotConnected)
	elseif result.ToolOnly ~= "" then
		local sentence = result.ToolOnly == "Failed" and CONFIG.Texts.ToolOnlyFailed or CONFIG.Texts.ToolOnlyDone
		if state.line then
			appendToLine(state.line, " " .. sentence)
		else
			addLine("assistant", assistantSpeaker(), sentence)
		end
	elseif not result.AnyText then
		addLine("system", "", CONFIG.Texts.NoAnswer)
	end
	refreshStatus()
end

local function sendToAssistant(text)
	if not assistant.IsAvailable then
		addLine("error", "", CONFIG.Texts.NotConnected)
		return
	end
	if assistant.IsBusy then
		addLine("error", "", CONFIG.Texts.Busy)
		return
	end
	local ok, turn = pcall(assistant.Send, assistant, text)
	if not ok then
		addLine("error", "", tostring(turn))
		return
	end
	local state = { turn = turn, line = nil }
	current = state
	turn.TextAdded:Connect(function(delta, startsNewMessage)
		if current ~= state then
			return
		end
		if startsNewMessage and state.line then
			state.line.streaming = false
			state.line = nil
		end
		if not state.line then
			state.line = addLine("assistant", assistantSpeaker(), "", true)
		end
		appendToLine(state.line, delta)
	end)
	turn.Ended:Connect(function(result)
		endTurn(state, result)
	end)
	refreshStatus()
end

-- ---- commands -------------------------------------------------------------------------------------------

local ownCommands = {}

local function reply(text, failed)
	addLine(failed and "error" or "command", "", text)
end

local function words(text)
	local list = {}
	for word in text:gmatch("%S+") do
		table.insert(list, word)
	end
	table.remove(list, 1)
	return list
end

local function commandList()
	local list = {}
	local seen = {}
	local function add(name, aliases, usage, description)
		local key = name:lower()
		if seen[key] then
			return
		end
		seen[key] = true
		table.insert(list, { name = name, aliases = aliases, usage = usage, description = description })
	end
	for _, command in ipairs(ownCommands) do
		add(command.name, command.aliases, command.usage, command.description)
	end
	for _, command in ipairs(TextChatService.TextChatCommands:GetChildren()) do
		local secondary = command.SecondaryAlias:gsub("^/", "")
		add(command.PrimaryAlias:gsub("^/", ""), secondary ~= "" and { secondary } or {},
			command:GetAttribute("Usage") or "", command:GetAttribute("Description") or "")
	end
	for _, command in ipairs(TextChatService:GetDescendants()) do
		if command:IsA("TextChatCommand") and command.Enabled and command.PrimaryAlias ~= "" then
			local secondary = command.SecondaryAlias:gsub("^/", "")
			add(command.PrimaryAlias:gsub("^/", ""), secondary ~= "" and { secondary } or {}, "",
				CONFIG.Texts.GameCommand)
		end
	end
	table.sort(list, function(a, b)
		return a.name:lower() < b.name:lower()
	end)
	return list
end

local function findCommand(name)
	local wanted = name:lower()
	for _, command in ipairs(commandList()) do
		if command.name:lower() == wanted then
			return command
		end
		for _, alias in ipairs(command.aliases) do
			if alias:lower() == wanted then
				return command
			end
		end
	end
	return nil
end

local function describe(command)
	local text = CONFIG.Prefix .. command.name
	if command.usage ~= "" then
		text = text .. " " .. command.usage
	end
	if command.description ~= "" then
		text = text .. " - " .. command.description
	end
	return text
end

local function addCommand(name, aliases, usage, description, run)
	table.insert(ownCommands, { name = name, aliases = aliases, usage = usage, description = description })
	local command = Instance.new("TextChatCommand")
	command.Name = "CoreAIChat" .. name
	command.PrimaryAlias = CONFIG.Prefix .. name
	if aliases[1] then
		command.SecondaryAlias = CONFIG.Prefix .. aliases[1]
	end
	command.Triggered:Connect(function(_, text)
		run(words(text))
	end)
	command.Parent = TextChatService
end

addCommand("help", { "?" }, "[command]", "List the chat commands, or describe one.", function(args)
	if args[1] then
		local wanted = args[1]:gsub("^/", "")
		local command = findCommand(wanted)
		if not command then
			reply(string.format("Unknown command /%s. Type /help for the list.", wanted), true)
			return
		end
		local text = describe(command)
		if #command.aliases > 0 then
			local shown = {}
			for _, alias in ipairs(command.aliases) do
				table.insert(shown, CONFIG.Prefix .. alias)
			end
			text = text .. "\nAliases: " .. table.concat(shown, ", ")
		end
		reply(text)
		return
	end
	local rows = { "Commands:" }
	for _, command in ipairs(commandList()) do
		table.insert(rows, describe(command))
	end
	reply(table.concat(rows, "\n"))
end)

addCommand("agent", {}, "[role]", "Show the agent that answers, or switch to another role.", function(args)
	local roles = table.concat(assistant:GetRoles(), ", ")
	if not args[1] then
		reply("Agent: " .. assistant.Role .. ". Available: " .. roles .. ".")
		return
	end
	local ok, role = assistant:SetRole(args[1])
	if ok then
		reply("Agent: " .. role .. ".")
	else
		reply("Unknown agent '" .. args[1] .. "'. Available: " .. roles .. ".", true)
	end
end)

addCommand("model", {}, "[profile|auto]", "Show the model profile of the active agent, or switch it.", function(args)
	if not assistant.CanSwitchModels then
		reply("Model switching is not available: no model routing is configured.", true)
		return
	end
	local profiles = assistant:GetModels()
	local function label(id)
		if id == "" then
			return "automatic"
		end
		for _, profile in ipairs(profiles) do
			if profile.Id == id then
				return profile.Name
			end
		end
		return id
	end
	local names = {}
	for _, profile in ipairs(profiles) do
		table.insert(names, profile.Name ~= profile.Id and (profile.Id .. " (" .. profile.Name .. ")") or profile.Id)
	end
	local listed = #names > 0 and table.concat(names, ", ") or "none yet (create one in Hub Settings)"
	local role = assistant.Role
	if not args[1] then
		reply("Model for " .. role .. ": " .. label(assistant:GetModel()) .. ". Profiles: " .. listed .. ".")
		return
	end
	local wanted = table.concat(args, " ")
	local id = nil
	local lowered = wanted:lower()
	if lowered == "auto" or lowered == "automatic" or lowered == "default" then
		id = ""
	else
		for _, profile in ipairs(profiles) do
			if profile.Id:lower() == lowered or profile.Name:lower() == lowered then
				id = profile.Id
				break
			end
		end
	end
	if id == nil then
		reply("Unknown model profile '" .. wanted .. "'. Profiles: " .. listed .. ".", true)
		return
	end
	local ok, message = assistant:SetModel(id)
	if ok then
		reply("Model for " .. role .. ": " .. label(id) .. ".")
	else
		reply("Could not switch the model of " .. role .. (message ~= "" and (": " .. message) or "."), true)
	end
end)

addCommand("stop", {}, "", "Stop the agent's current answer.", function()
	if current then
		current.turn:Cancel()
	elseif assistant.IsBusy then
		assistant:Stop()
	else
		reply(CONFIG.Texts.NothingToStop)
	end
end)

-- ---- what the player types ------------------------------------------------------------------------------

local function trim(text)
	return (text:gsub("^%s+", ""):gsub("%s+$", ""))
end

local function submit(raw)
	local text = trim(raw or "")
	if text == "" then
		return
	end
	if #text > CONFIG.MaxInput then
		text = text:sub(1, CONFIG.MaxInput)
	end
	local prefix = CONFIG.Prefix
	if text:sub(1, 1) == prefix and text:sub(2, 2) ~= prefix then
		local name = text:sub(2):match("^(%S*)")
		if name == "" then
			reply(CONFIG.Texts.EmptyCommand, true)
		elseif not findCommand(name) then
			reply(CONFIG.Texts.UnknownCommand:format(name), true)
		else
			general:SendAsync(text)
		end
		return
	end
	-- WHY the raw line: the chat's parser turns a doubled prefix into one, so "//help me" reaches the AI as text.
	local message = general:SendAsync(text)
	if message.Metadata == "CoreAI.Taken" or message.Metadata == "CoreAI.Command" then
		return
	end
	sendToAssistant(message.Text)
end

local function setOpen(value, prefill)
	open = value
	input.Visible = value
	feed.Interactable = value
	feed.ScrollBarThickness = value and 6 or 0
	if value then
		input.Text = prefill or ""
		input:CaptureFocus()
	else
		input.Text = ""
		stickToBottom = true
		scrollToBottom()
	end
	refreshVisibility()
end

local function isOpenKey(name)
	if CONFIG.OpenKeys[name] then
		return true
	end
	local extra = inputBar.KeyboardKeyCode
	return extra ~= nil and extra ~= Enum.KeyCode.Unknown and extra.Name == name
end

UserInputService.InputBegan:Connect(function(inputObject, gameProcessed)
	if inputObject.UserInputType ~= Enum.UserInputType.Keyboard then
		return
	end
	local name = inputObject.KeyCode.Name
	if open then
		if name == CONFIG.CloseKey then
			input:ReleaseFocus(false)
		end
		return
	end
	if gameProcessed then
		return
	end
	if CONFIG.CommandKeys[name] then
		setOpen(true, CONFIG.Prefix)
	elseif isOpenKey(name) then
		setOpen(true, "")
	end
end)

input.FocusLost:Connect(function(enterPressed)
	if not open then
		return
	end
	local text = input.Text
	setOpen(false)
	if enterPressed then
		submit(text)
	end
end)

feed:GetPropertyChangedSignal("CanvasPosition"):Connect(function()
	if open then
		local bottom = feed.AbsoluteCanvasSize.Y - feed.AbsoluteWindowSize.Y
		stickToBottom = feed.CanvasPosition.Y >= bottom - 2
	end
end)

-- ---- lines from the world -------------------------------------------------------------------------------

local KINDS = {
	["CoreAI.Command"] = "command",
	["CoreAI.Error"] = "error",
	["CoreAI.Assistant"] = "assistant",
}

TextChatService.MessageReceived:Connect(function(message)
	local source = message.TextSource
	if source then
		local speaker = source.UserId == player.UserId and CONFIG.PlayerName or source.Name
		addLine("player", speaker, message.Text)
		return
	end
	addLine(KINDS[message.Metadata] or "system", message.PrefixText, message.Text)
end)

local lastRefresh = 0
RunService.Heartbeat:Connect(function()
	local now = os.clock()
	if now - lastRefresh < 0.1 then
		return
	end
	lastRefresh = now
	refreshVisibility()
	refreshStatus()
end)
