--[[@coreai
id: blockworld_hud
name: Block World 3 - HUD and hotbar
version: 1.2.2
active: false
capabilities: All
category: Samples/Block World
author: CoreAI
description: Part of the Block World demo. The whole interface, made in this mod from UXML and USS text through UxmlService - a crosshair, a nine-slot hotbar (keys 1-9, the mouse wheel or a click on a slot), a key legend, the clock, creative or survival, your health in survival, a placed and broken counter, the block counts in survival, the autopilot banner, short notices from the other Block World mods and the Building the world veil a new player waits behind while the first view is made. Choosing a slot publishes the workspace attribute BlockWorldSelected, which the build and torch mods read. Hot-reload it to restyle the HUD live.
]]

-- The hotbar is the single owner of "what is in your hand": a slot is chosen by number key, wheel or click,
-- and the choice is published as an attribute, so the mods that act on it never talk to each other. Everything
-- else on screen is a view of attributes the other mods publish; each label is rewritten only when its text
-- changes, so an idle frame writes nothing to the UI.

local Lighting = game:GetService("Lighting")
local RunService = game:GetService("RunService")
local UserInputService = game:GetService("UserInputService")
local UxmlService = game:GetService("UxmlService")

local NOTICE_SECONDS = 4

-- The nine hotbar items: an Enum.Material name for a block, or "Torch" (blockworld_torches).
local ITEMS = {
    { id = "Grass", label = "Grass", color = "rgb(96, 166, 64)" },
    { id = "Ground", label = "Dirt", color = "rgb(122, 86, 56)" },
    { id = "Rock", label = "Stone", color = "rgb(128, 128, 132)" },
    { id = "Cobblestone", label = "Cobblestone", color = "rgb(104, 106, 112)" },
    { id = "WoodPlanks", label = "Planks", color = "rgb(176, 132, 78)" },
    { id = "Brick", label = "Brick", color = "rgb(168, 74, 58)" },
    { id = "Sand", label = "Sand", color = "rgb(222, 206, 150)" },
    { id = "LeafyGrass", label = "Leaves", color = "rgb(58, 120, 46)" },
    { id = "Torch", label = "Torch", color = "rgb(255, 176, 60)" },
}

local NUMBER_KEYS = {
    One = 1, Two = 2, Three = 3, Four = 4, Five = 5, Six = 6, Seven = 7, Eight = 8, Nine = 9,
}

local slotMarkup = {}
for index, item in ipairs(ITEMS) do
    slotMarkup[#slotMarkup + 1] = '    <ui:Button name="slot' .. index .. '" class="slot">\n'
        .. '      <ui:VisualElement name="swatch' .. index .. '" class="swatch" picking-mode="Ignore" style="background-color: '
        .. item.color .. ';" />\n'
        .. '      <ui:Label name="key' .. index .. '" class="key" text="' .. index .. '" picking-mode="Ignore" />\n'
        .. '      <ui:Label name="count' .. index .. '" class="count" text="" picking-mode="Ignore" />\n'
        .. '    </ui:Button>\n'
end

-- The key legend: what a new player needs on screen, one key per line.
local LEGEND = {
    { "Left click", "break" }, { "Right click", "place" }, { "1-9, wheel", "choose a block" },
    { "WASD, Space", "walk, jump" }, { "Ctrl, W twice", "sprint" }, { "Space twice", "fly" },
    { "T", "talk to the AI" },
    { "E", "talk to a villager" }, { "P", "autopilot" }, { "L", "time of day" }, { "G", "creative or survival" },
}

local legendMarkup = {}
for index, entry in ipairs(LEGEND) do
    legendMarkup[#legendMarkup + 1] = '    <ui:VisualElement class="legend-row" picking-mode="Ignore">'
        .. '<ui:Label name="legendkey' .. index .. '" class="legend-key" text="' .. entry[1] .. '" picking-mode="Ignore" />'
        .. '<ui:Label class="legend-text" text="' .. entry[2] .. '" picking-mode="Ignore" /></ui:VisualElement>\n'
end

local UXML = [[
<ui:UXML xmlns:ui="UnityEngine.UIElements">
  <ui:Label name="crosshair" class="crosshair" text="+" picking-mode="Ignore" />
  <ui:VisualElement name="status" class="status" picking-mode="Ignore">
    <ui:Label name="clock" class="chip" text="--:--" picking-mode="Ignore" />
    <ui:Label name="mode" class="chip" text="Creative" picking-mode="Ignore" />
    <ui:Label name="health" class="chip health is-hidden" text="" picking-mode="Ignore" />
    <ui:Label name="counter" class="chip" text="Placed 0  Broken 0" picking-mode="Ignore" />
  </ui:VisualElement>
  <ui:Label name="autopilot" class="banner is-hidden" text="" picking-mode="Ignore" />
  <ui:Label name="notice" class="notice is-hidden" text="" picking-mode="Ignore" />
  <ui:Label name="itemname" class="itemname" text="" picking-mode="Ignore" />
  <ui:VisualElement name="hotbar" class="hotbar">
]] .. table.concat(slotMarkup) .. [[
  </ui:VisualElement>
  <ui:VisualElement name="help" class="help" picking-mode="Ignore">
]] .. table.concat(legendMarkup) .. [[
  </ui:VisualElement>
  <ui:VisualElement name="veil" class="veil is-hidden" picking-mode="Ignore">
    <ui:Label name="veiltitle" class="veil-title" text="Building the world" picking-mode="Ignore" />
    <ui:Label name="veilprogress" class="veil-progress" text="" picking-mode="Ignore" />
    <ui:Label name="veiltip" class="veil-tip" text="Left click breaks a block, right click places one. T talks to the AI." picking-mode="Ignore" />
  </ui:VisualElement>
</ui:UXML>
]]

local USS = [[
.crosshair { position: absolute; left: 50%; top: 50%; translate: -50% -50%; font-size: 30px; color: rgb(255, 255, 255);
  -unity-text-outline-width: 1px; -unity-text-outline-color: rgba(0, 0, 0, 0.85); }
.status { position: absolute; top: 14px; left: 14px; flex-direction: row; }
.chip { margin-right: 8px; padding: 4px 10px 4px 10px; border-radius: 6px; background-color: rgba(10, 14, 24, 0.62);
  color: rgb(240, 240, 240); font-size: 16px; -unity-font-style: bold; }
.banner { position: absolute; top: 14px; right: 14px; padding: 4px 10px 4px 10px; border-radius: 6px;
  background-color: rgba(30, 96, 170, 0.85); color: rgb(255, 255, 255); font-size: 16px; -unity-font-style: bold; }
.notice { position: absolute; top: 60px; left: 50%; translate: -50% 0; padding: 6px 14px 6px 14px; border-radius: 6px;
  background-color: rgba(10, 14, 24, 0.75); color: rgb(255, 228, 140); font-size: 16px; }
.itemname { position: absolute; bottom: 92px; left: 50%; translate: -50% 0; font-size: 18px; color: rgb(255, 255, 255);
  -unity-font-style: bold; text-shadow: 1px 1px 2px rgba(0, 0, 0, 0.9); }
.hotbar { position: absolute; bottom: 14px; left: 50%; translate: -50% 0; flex-direction: row; padding: 4px;
  border-radius: 8px; background-color: rgba(10, 12, 18, 0.55); }
.slot { width: 60px; height: 60px; margin: 3px; padding: 0; border-width: 2px; border-radius: 5px;
  border-color: rgba(255, 255, 255, 0.2); background-color: rgba(30, 32, 40, 0.7); }
.slot:hover { border-color: rgba(255, 255, 255, 0.6); }
.slot.is-selected { border-width: 3px; border-color: rgb(255, 255, 255); scale: 1.1; transition: scale 0.08s ease-out; }
.swatch { position: absolute; left: 10px; top: 10px; right: 10px; bottom: 10px; border-radius: 4px; }
.key { position: absolute; left: 4px; top: 1px; font-size: 11px; color: rgba(255, 255, 255, 0.85); }
.count { position: absolute; right: 4px; bottom: 1px; font-size: 13px; color: rgb(255, 255, 255); -unity-font-style: bold;
  -unity-text-outline-width: 1px; -unity-text-outline-color: rgb(0, 0, 0); }
.help { position: absolute; bottom: 14px; right: 14px; padding: 8px 12px 8px 12px; border-radius: 8px;
  background-color: rgba(10, 14, 24, 0.6); }
.legend-row { flex-direction: row; }
.legend-key { width: 110px; font-size: 16px; color: rgb(255, 214, 120); -unity-font-style: bold; }
.legend-text { font-size: 16px; color: rgb(240, 240, 240); }
.health { color: rgb(255, 150, 150); }
.health.is-low { background-color: rgba(150, 24, 24, 0.85); color: rgb(255, 255, 255); }
.veil { position: absolute; left: 0; top: 0; right: 0; bottom: 0; align-items: center; justify-content: center;
  background-color: rgb(184, 206, 207); }
.veil-title { font-size: 46px; color: rgb(34, 52, 64); -unity-font-style: bold;
  text-shadow: 1px 1px 3px rgba(255, 255, 255, 0.5); }
.veil-progress { margin-top: 10px; font-size: 20px; color: rgba(34, 52, 64, 0.92); }
.veil-tip { margin-top: 36px; font-size: 18px; color: rgba(34, 52, 64, 0.85); }
.is-hidden { display: none; }
]]

local hud = UxmlService:CreateScreen("hud", UXML, USS, { SortOrder = 10 })
for _, report in ipairs(hud.Diagnostics) do
    warn("[blockworld] HUD " .. report.Code .. " line " .. report.Line .. ": " .. report.Message)
end

local slots, counts = {}, {}
for index = 1, #ITEMS do
    slots[index] = hud:GetElement("slot" .. index)
    counts[index] = hud:GetElement("count" .. index)
end
local clockLabel = hud:GetElement("clock")
local modeLabel = hud:GetElement("mode")
local healthLabel = hud:GetElement("health")
local counterLabel = hud:GetElement("counter")
local noticeLabel = hud:GetElement("notice")
local autopilotLabel = hud:GetElement("autopilot")
local itemLabel = hud:GetElement("itemname")
local veil = hud:GetElement("veil")
local veilProgress = hud:GetElement("veilprogress")

local shown = {}
local function setText(label, key, text)
    if shown[key] ~= text then
        shown[key] = text
        label.Text = text
    end
end

-- ---- the hotbar ------------------------------------------------------------------------------------

local selected = 0
local function choose(index)
    index = (index - 1) % #ITEMS + 1
    if index == selected then
        return
    end
    if selected > 0 then
        slots[selected]:RemoveClass("is-selected")
    end
    selected = index
    slots[index]:AddClass("is-selected")
    setText(itemLabel, "item", ITEMS[index].label)
    workspace:SetAttribute("BlockWorldSlot", index)
    workspace:SetAttribute("BlockWorldSelected", ITEMS[index].id)
end

for index, slot in ipairs(slots) do
    slot.Clicked:Connect(function()
        choose(index)
    end)
end

UserInputService.InputBegan:Connect(function(input, gameProcessed)
    if gameProcessed then
        return
    end
    local number = NUMBER_KEYS[input.KeyCode.Name]
    if number then
        choose(number)
    end
end)

UserInputService.InputChanged:Connect(function(input, gameProcessed)
    if gameProcessed or input.UserInputType ~= Enum.UserInputType.MouseWheel then
        return
    end
    -- WHY up means left: the Minecraft convention; Position.Z is +1 for a notch away from the player.
    choose(selected + (input.Position.Z > 0 and -1 or 1))
end)

local startSlot = workspace:GetAttribute("BlockWorldSlot")
choose(type(startSlot) == "number" and math.clamp(math.floor(startSlot), 1, #ITEMS) or 1)

-- ---- the status line --------------------------------------------------------------------------------

local function twoDigits(value)
    return value < 10 and "0" .. value or tostring(value)
end

local function refreshClock()
    local minutes = math.floor(Lighting.ClockTime * 60 + 0.5) % 1440
    local hours = math.floor(minutes / 60)
    local night = Lighting.ClockTime < 6 or Lighting.ClockTime >= 18.5
    setText(clockLabel, "clock", twoDigits(hours) .. ":" .. twoDigits(minutes % 60) .. (night and "  Night" or "  Day"))
end

local function refreshStatus()
    local creative = workspace:GetAttribute("BlockWorldCreative") ~= false
    setText(modeLabel, "mode", creative and "Creative" or "Survival")
    local health = workspace:GetAttribute("BlockWorldHealth")
    local maxHealth = workspace:GetAttribute("BlockWorldMaxHealth")
    if creative or type(health) ~= "number" or type(maxHealth) ~= "number" then
        healthLabel:AddClass("is-hidden")
    else
        healthLabel:RemoveClass("is-hidden")
        setText(healthLabel, "health", "Health " .. health .. " / " .. maxHealth)
        if health < maxHealth * 0.3 then
            healthLabel:AddClass("is-low")
        else
            healthLabel:RemoveClass("is-low")
        end
    end
    setText(counterLabel, "counter", "Placed " .. (workspace:GetAttribute("BlockWorldPlaced") or 0)
        .. "  Broken " .. (workspace:GetAttribute("BlockWorldBroken") or 0))
    for index, item in ipairs(ITEMS) do
        local count = workspace:GetAttribute("BlockWorldCount_" .. item.id)
        local text = ""
        if not creative and item.id ~= "Torch" then
            text = tostring(count or 0)
        end
        setText(counts[index], "count" .. index, text)
    end
end

local noticeTimer = 0
local function showNotice()
    local text = workspace:GetAttribute("BlockWorldNotice")
    if type(text) ~= "string" or text == "" then
        return
    end
    setText(noticeLabel, "notice", text)
    noticeLabel:RemoveClass("is-hidden")
    noticeTimer = NOTICE_SECONDS
end

local function refreshAutopilot()
    local text = workspace:GetAttribute("BlockWorldAutopilot")
    if type(text) == "string" and text ~= "" then
        setText(autopilotLabel, "autopilot", "Autopilot: " .. text)
        autopilotLabel:RemoveClass("is-hidden")
    else
        autopilotLabel:AddClass("is-hidden")
    end
end

for _, name in ipairs({ "BlockWorldCreative", "BlockWorldPlaced", "BlockWorldBroken", "BlockWorldHealth",
    "BlockWorldMaxHealth" }) do
    workspace:GetAttributeChangedSignal(name):Connect(refreshStatus)
end
for _, item in ipairs(ITEMS) do
    workspace:GetAttributeChangedSignal("BlockWorldCount_" .. item.id):Connect(refreshStatus)
end
workspace:GetAttributeChangedSignal("BlockWorldNoticeAt"):Connect(showNotice)
workspace:GetAttributeChangedSignal("BlockWorldAutopilot"):Connect(refreshAutopilot)

-- The veil covers the screen while the world mod holds a new player for the first view (BlockWorldOpened false),
-- so the first thing seen is the land around the spawn, not the void around one column.
-- WHY its colour (.veil): the default look's haze just below the horizon at 10:00, so it lifts into the same sky.
local function refreshVeil()
    if workspace:GetAttribute("BlockWorldOpened") ~= false then
        veil:AddClass("is-hidden")
        return
    end
    veil:RemoveClass("is-hidden")
    local made = workspace:GetAttribute("BlockWorldColumns") or 0
    local needed = workspace:GetAttribute("BlockWorldOpeningColumns") or 0
    setText(veilProgress, "veilprogress", needed > 0
        and ("The land around you: " .. math.min(made, needed) .. " of " .. needed)
        or "The land around you")
end
for _, name in ipairs({ "BlockWorldOpened", "BlockWorldColumns", "BlockWorldOpeningColumns" }) do
    workspace:GetAttributeChangedSignal(name):Connect(refreshVeil)
end

local clockTimer = 0
RunService.Heartbeat:Connect(function(dt)
    clockTimer = clockTimer - dt
    if clockTimer <= 0 then
        clockTimer = 0.25
        refreshClock()
    end
    if noticeTimer > 0 then
        noticeTimer = noticeTimer - dt
        if noticeTimer <= 0 then
            noticeLabel:AddClass("is-hidden")
        end
    end
end)

refreshClock()
refreshStatus()
refreshAutopilot()
refreshVeil()
