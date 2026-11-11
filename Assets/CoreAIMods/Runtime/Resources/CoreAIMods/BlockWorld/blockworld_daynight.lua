--[[@coreai
id: blockworld_daynight
name: Block World 4 - Day and night
version: 1.0.0
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo. A day lasts eight minutes - the mod advances Lighting.ClockTime and darkens the ambient light at night, so torches matter after dusk. Set the time with /time noon, /time 18:30, /time stop or /time go where the host offers chat commands, with the L key (noon, dusk, midnight, dawn), or from any script or the AI by setting the workspace attribute BlockWorldTime to the same words. A ClockTime written by someone else is kept: the clock carries on from it.
]]

-- The clock reads Lighting.ClockTime back before every step, so a time set by the AI, another mod or a saved
-- world is adopted rather than overwritten. The sun follows ClockTime by itself; what this mod adds is the
-- ambient light, which Roblox games lower at night themselves (Lighting.Ambient and OutdoorAmbient).

local Lighting = game:GetService("Lighting")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")

local DAY_SECONDS = 480          -- one whole day, in real seconds
local STEP_SECONDS = 0.1         -- how often the clock is written
local NAMED = {
    dawn = 6, morning = 8, day = 9, noon = 12, afternoon = 15,
    dusk = 18.5, evening = 19.5, night = 21, midnight = 0,
}
local CYCLE = { "noon", "dusk", "midnight", "dawn" }

local DAY_OUTDOOR = Color3.fromRGB(150, 150, 150)
local NIGHT_OUTDOOR = Color3.fromRGB(34, 40, 74)
local DAY_AMBIENT = Color3.fromRGB(110, 110, 116)
local NIGHT_AMBIENT = Color3.fromRGB(14, 16, 30)

local paused = false
local cycleIndex = 0

local function notify(text)
    workspace:SetAttribute("BlockWorldNotice", text)
    workspace:SetAttribute("BlockWorldNoticeAt", (workspace:GetAttribute("BlockWorldNoticeAt") or 0) + 1)
end

-- Hours (0 <= h < 24) for a time word, "HH:MM" or a number of hours; nil when it is none of them.
local function parseTime(text)
    local word = string.lower(tostring(text or "")):match("^%s*(.-)%s*$")
    if NAMED[word] then
        return NAMED[word]
    end
    local hours, minutes = word:match("^(%d%d?):(%d%d)$")
    if hours then
        hours, minutes = tonumber(hours), tonumber(minutes)
        if hours < 24 and minutes < 60 then
            return hours + minutes / 60
        end
        return nil
    end
    local number = tonumber(word)
    if number and number >= 0 and number < 24 then
        return number
    end
    return nil
end

local function smoothstep(edge0, edge1, x)
    local t = math.clamp((x - edge0) / (edge1 - edge0), 0, 1)
    return t * t * (3 - 2 * t)
end

-- 0 at night, 1 by day, easing through dawn (5:00-7:30) and dusk (17:30-20:00).
local function daylight(hours)
    if hours < 12 then
        return smoothstep(5, 7.5, hours)
    end
    return 1 - smoothstep(17.5, 20, hours)
end

local shownDaylight = -1
local function applyAmbient(hours)
    local light = daylight(hours)
    if math.abs(light - shownDaylight) < 0.01 then
        return
    end
    shownDaylight = light
    Lighting.OutdoorAmbient = NIGHT_OUTDOOR:Lerp(DAY_OUTDOOR, light)
    Lighting.Ambient = NIGHT_AMBIENT:Lerp(DAY_AMBIENT, light)
end

local function twoDigits(value)
    return value < 10 and "0" .. value or tostring(value)
end

local function describe(hours)
    local minutes = math.floor(hours * 60 + 0.5) % 1440
    return twoDigits(math.floor(minutes / 60)) .. ":" .. twoDigits(minutes % 60)
end

-- One entry point for /time, the L key and the BlockWorldTime attribute.
local function runTimeCommand(text)
    local word = string.lower(tostring(text or "")):match("^%s*(.-)%s*$")
    if word == "" then
        notify("It is " .. describe(Lighting.ClockTime) .. ". Try /time noon, /time 18:30, /time stop or /time go.")
        return
    end
    if word == "stop" or word == "go" then
        paused = word == "stop"
        notify(paused and "Time stands still." or "Time runs again.")
        return
    end
    local hours = parseTime(word)
    if not hours then
        notify("Unknown time '" .. word .. "' - try dawn, noon, dusk, midnight or 18:30.")
        return
    end
    Lighting.ClockTime = hours
    applyAmbient(hours)
    notify("Time set to " .. describe(hours) .. ".")
end

-- Roblox's chat command, where the host has TextChatService; CoreAI's chat has no Lua commands yet.
local commandOk = pcall(function()
    local TextChatService = game:GetService("TextChatService")
    local command = Instance.new("TextChatCommand")
    command.Name = "BlockWorldTime"
    command.PrimaryAlias = "/time"
    command.Parent = TextChatService:FindFirstChild("TextChatCommands") or TextChatService
    command.Triggered:Connect(function(_, text)
        runTimeCommand((text or ""):gsub("^%s*/%a+", ""))
    end)
end)
if not commandOk then
    print("[blockworld] /time needs Lua chat commands (TextChatService), which this host does not offer yet - "
        .. "press L, or set the workspace attribute BlockWorldTime to noon, dusk, midnight or 18:30.")
end

workspace:GetAttributeChangedSignal("BlockWorldTime"):Connect(function()
    local text = workspace:GetAttribute("BlockWorldTime")
    if type(text) == "string" and text ~= "" then
        workspace:SetAttribute("BlockWorldTime", "")
        runTimeCommand(text)
    end
end)

UserInputService.InputBegan:Connect(function(input, gameProcessed)
    if gameProcessed or input.KeyCode ~= Enum.KeyCode.L then
        return
    end
    cycleIndex = cycleIndex % #CYCLE + 1
    runTimeCommand(CYCLE[cycleIndex])
end)

local pending = 0
RunService.Heartbeat:Connect(function(dt)
    pending = pending + dt
    if pending < STEP_SECONDS then
        return
    end
    local hours = Lighting.ClockTime
    if not paused then
        hours = (hours + pending * 24 / DAY_SECONDS) % 24
        Lighting.ClockTime = hours
    end
    pending = 0
    applyAmbient(hours)
end)

applyAmbient(Lighting.ClockTime)
print("[blockworld] day and night: " .. describe(Lighting.ClockTime) .. ", a day lasts " .. math.floor(DAY_SECONDS / 60)
    .. " minutes. L changes the time.")
