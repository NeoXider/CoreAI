--[[@coreai
id: blockworld_daynight
name: Block World 4 - Day and night
version: 1.4.1
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo. A day lasts eight minutes and a fresh world starts at 10:00 - the mod advances Lighting.ClockTime and sets the sky's light (Lighting.OutdoorAmbient): bright by day, warm and still readable at dusk, a dim blue moonlight at night, while Lighting.Ambient, the light a surface the sky does not reach gets, stays low all day, so torches matter after dusk and in caves at any hour. The fog (Lighting.FogColor, FogStart, FogEnd) takes the sky's colour - pale blue by day, warm at dawn and dusk, dark at night - and is whole where the streamed land ends, so nobody sees its edge. Set the time with /time noon, /time 18:30, /time stop or /time go where the host offers chat commands, with the L key (noon, dusk, midnight, dawn), or from any script or the AI by setting the workspace attribute BlockWorldTime to the same words. A ClockTime written by someone else is kept: the clock carries on from it.
]]

-- The clock reads Lighting.ClockTime back before every step, so a time set by the AI, another mod or a saved
-- world is adopted rather than overwritten. The sun follows ClockTime by itself; what this mod adds is the
-- ambient light, with Roblox's two roles: OutdoorAmbient is the sky's light on every surface under open sky (a
-- hillside in shadow included), lowered at night as Roblox games do; Ambient is what a surface the sky does not
-- reach gets (a cave, a covered room), and stays low at every hour, so a cave is dark at noon. The fog is whole
-- just short of the stream radius blockworld_world publishes (BlockWorldViewStuds); without that attribute the
-- distances are left to whoever set them.

local Lighting = game:GetService("Lighting")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")

local DAY_SECONDS = 480          -- one whole day, in real seconds
local START_HOURS = 10           -- where a fresh world's clock starts: a mid-morning sun, high enough to light the
                                 -- hills and low enough to give them shadows
local STEP_SECONDS = 0.1         -- how often the clock is written
local NAMED = {
    dawn = 6, morning = 8, day = 9, noon = 12, afternoon = 15,
    dusk = 18.5, evening = 19.5, night = 21, midnight = 0,
}
local CYCLE = { "noon", "dusk", "midnight", "dawn" }

-- The sky's light on the land, as a player should see it. WHY this bright by day: it is all a face turned from the
-- sun gets, so with less the hillsides in shadow read nearly black. WHY this bright at dusk: the sun is down by
-- 18:30, so this is all the land gets, and the ground stays readable. WHY blue and dim at night: a block game's night
-- is moonlit, readable near you and far darker than a torch.
local DAY_SKY = Color3.fromRGB(195, 196, 204)
local DUSK_SKY = Color3.fromRGB(215, 190, 165)
local NIGHT_SKY = Color3.fromRGB(80, 100, 150)
-- The host draws OutdoorAmbient scaled by the sun's height: all of it with the sun 6 degrees up, 15% of it once the
-- sun is 6 degrees down (CoreAI's twilight). The mod asks for the light it wants seen, so it divides by that share.
-- TODO: drop the division if the host stops scaling OutdoorAmbient by its own twilight.
local HOST_NIGHT_SHARE = 0.15
local HOST_TWILIGHT_DEGREES = 6
-- WHY this low: a cave is dark at any hour without a torch, so a torch matters there. On the default look the walls
-- stay black up to about 56,60,80 and show faintly only near 80,84,104, which is brighter than the open night.
local AMBIENT = Color3.fromRGB(22, 24, 34)
-- WHY these: the haze of the default look at the horizon by day, a warm dusk, and a night sky dark enough for torches.
local DAY_FOG = Color3.fromRGB(198, 220, 228)
-- WHY this dark at dusk: the fog is drawn unlit, so a bright dusk haze turns far trees into cut-outs over the land.
-- WHY blue: the haze and the horizon round the camera take this colour, and a dusk sky reads blue, not brown.
local DUSK_FOG = Color3.fromRGB(52, 58, 92)
local NIGHT_FOG = Color3.fromRGB(24, 32, 56)
local FOG_EDGE = 6               -- studs short of the stream radius where the fog is whole
local FOG_START_SHARE = 0.7      -- where it begins, as a share of where it is whole: 122 of 175 on a desktop

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

-- WHY the land's own radius: past it there is nothing, so a fog that ends later shows the world's edge; the WebGL
-- player streams fewer columns and gets a nearer fog with no change here.
local function applyFogDistance()
    local reach = workspace:GetAttribute("BlockWorldViewStuds")
    if type(reach) ~= "number" or reach <= FOG_EDGE then
        return
    end
    Lighting.FogEnd = reach - FOG_EDGE
    Lighting.FogStart = (reach - FOG_EDGE) * FOG_START_SHARE
end

-- The share of OutdoorAmbient the host draws at the sun's height now (see HOST_NIGHT_SHARE).
local function hostSkyShare()
    local sun = Lighting:GetSunDirection()
    local elevation = math.deg(math.asin(math.clamp(sun.Y, -1, 1)))
    return HOST_NIGHT_SHARE
        + (1 - HOST_NIGHT_SHARE) * smoothstep(-HOST_TWILIGHT_DEGREES, HOST_TWILIGHT_DEGREES, elevation)
end

local shownDaylight = -1
local function applyAmbient(hours)
    local light = daylight(hours)
    if math.abs(light - shownDaylight) < 0.01 then
        return
    end
    shownDaylight = light
    -- WHY dusk peaks half way through the light's fall (18:45, and 6:15 at dawn): the warm colours are reached there.
    local dusk = 1 - math.abs(light * 2 - 1)
    local sky = NIGHT_SKY:Lerp(DAY_SKY, light):Lerp(DUSK_SKY, dusk)
    local share = hostSkyShare()
    Lighting.OutdoorAmbient = Color3.new(sky.R / share, sky.G / share, sky.B / share)
    Lighting.Ambient = AMBIENT
    -- WHY the day's share squared: the unlit fog must darken faster than the lit land as the light falls.
    Lighting.FogColor = NIGHT_FOG:Lerp(DAY_FOG, light * light):Lerp(DUSK_FOG, dusk)
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

-- /time is a Roblox chat command (a TextChatCommand under TextChatService); a host without Lua chat commands still
-- has the L key and the BlockWorldTime attribute.
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

-- WHY a mark on the workspace: a reload of this mod (an AI edit, a hot reload) must keep the time of day, and
-- only a world that never ran the clock starts in the morning.
if workspace:GetAttribute("BlockWorldClockStarted") ~= true then
    workspace:SetAttribute("BlockWorldClockStarted", true)
    Lighting.ClockTime = START_HOURS
end
applyAmbient(Lighting.ClockTime)
applyFogDistance()
workspace:GetAttributeChangedSignal("BlockWorldViewStuds"):Connect(applyFogDistance)
print("[blockworld] day and night: " .. describe(Lighting.ClockTime) .. ", a day lasts " .. math.floor(DAY_SECONDS / 60)
    .. " minutes. L changes the time.")
