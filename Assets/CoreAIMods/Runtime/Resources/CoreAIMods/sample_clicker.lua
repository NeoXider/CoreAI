--[[@coreai
id: sample_clicker
name: Block Clicker (sample)
version: 1.5.0
active: false
capabilities: All
category: Samples
author: CoreAI
description: Opt-in playable sample. A 3D block clicker you play with the MOUSE. CLICK THE BIG GOLD BLOCK to mine coins. CLICK THE LEFT block to buy the click upgrade (the gold block grows and each click is worth more); CLICK THE RIGHT block to buy the passive upgrade (coins tick in on their own). The upgrade blocks are always clickable: when you can afford it the block glows green and clicking it POPS green (purchase); when you cannot, clicking it FLASHES red (can't afford). Nothing happens when you click empty space - only the actual block under the cursor reacts (real 3D click-picking via ClickDetector). Coins are shown as physical blocks (bronze ones, silver tens, gold hundreds, platinum thousands) and each upgrade's PRICE is a little stack of the same blocks under its button. Pure Roblox API. Ships disabled; enable it from the Hub Mods tab.
]]

-- A complete idle/clicker game rendered entirely with 3D blocks (no UI) in the SAME API Roblox uses.
-- Input is real 3D click-picking: each clickable Part carries a child ClickDetector and its MouseClick
-- signal drives the game. The loop is RunService.Heartbeat(dt): passive income accrues by dt, the clicker
-- "pops" on each click, and each upgrade block animates - green success pop on a purchase, red flash on a
-- can't-afford click - so every click gives feedback whether or not you could pay for it.

local RunService = game:GetService("RunService")

local root = Instance.new("Folder")
root.Name = "BlockClicker"
root.Parent = workspace

local cam = workspace.CurrentCamera
cam.CameraType = Enum.CameraType.Scriptable
cam.CFrame = CFrame.lookAt(Vector3.new(0, 6, 22), Vector3.new(0, 4, 0))

-- ---- coin tiers (shared by the coin pile and the price tags) ----------------------------------
local TIERS = {
    { div = 1000, size = 1.7, color = Color3.fromRGB(150, 230, 255) }, -- platinum thousands
    { div = 100,  size = 1.3, color = Color3.fromRGB(255, 215, 0) },   -- gold hundreds
    { div = 10,   size = 0.9, color = Color3.fromRGB(200, 200, 210) }, -- silver tens
    { div = 1,    size = 0.55, color = Color3.fromRGB(205, 130, 70) }, -- bronze ones
}

-- ---- game state -------------------------------------------------------------------------------
local coins = 0
local clickPower = 1
local passive = 0
local activeCost = 10
local passiveCost = 25
local clickPop = 0
local flash = 0
local shownCoins, shownActive, shownPassive = -1, -1, -1
local passiveAccum = 0

-- ---- helper: give a Part a child ClickDetector wired to `onClick` -----------------------------
local function make_clickable(part, onClick)
    local cd = Instance.new("ClickDetector")
    cd.MaxActivationDistance = 64        -- studs; generous so the whole board is reachable
    cd.Parent = part                     -- ClickDetector is a CHILD of the clickable part
    cd.MouseClick:Connect(onClick)
    return cd
end

-- ---- clicker block ----------------------------------------------------------------------------
local BASE_GOLD = Color3.fromRGB(255, 190, 40)
local clicker = Instance.new("Part")
clicker.Name = "Clicker"
clicker.Color = BASE_GOLD
clicker.Anchored = true
clicker.Position = Vector3.new(0, 4, 0)
clicker.Parent = root
local function clicker_base_size() return 2 + (clickPower - 1) * 0.4 end

-- ---- upgrade buttons --------------------------------------------------------------------------
local IDLE_COLOR = Color3.fromRGB(90, 90, 110)     -- gray: can't afford
local AFFORD_COLOR = Color3.fromRGB(60, 210, 90)   -- green: can afford
local BUY_FLASH = Color3.fromRGB(235, 255, 235)    -- near-white: successful purchase pop
local REJECT_RED = Color3.fromRGB(235, 60, 60)     -- red: clicked but can't afford
local BTN_BASE = 2.6
local function make_button(x)
    local b = Instance.new("Part")
    b.Name = "UpgradeButton"
    b.Size = Vector3.new(BTN_BASE, BTN_BASE, 1)
    b.Color = IDLE_COLOR
    b.Position = Vector3.new(x, 2, 0)
    b.Anchored = true
    b.Parent = root
    -- buyPop = green success animation, reject = red can't-afford animation, base = eased idle/afford color,
    -- earn = green pulse each time this upgrade earns a coin, grow/growTarget = persistent size that scales
    -- with the upgrade's level (so a more-upgraded block is visibly bigger).
    return { part = b, buyPop = 0, reject = 0, base = IDLE_COLOR, earn = 0, grow = 1, growTarget = 1 }
end
local activeBtn = make_button(-8)    -- click = buy click upgrade
local passiveBtn = make_button(8)    -- click = buy passive upgrade

-- ---- block price tags + coin pile -------------------------------------------------------------
-- Draws `n` as a left-to-right stack of tier blocks starting at (x0, y), scaled by `scale`, into the
-- given part list (cleared first). Shared by the big coin pile and the small per-button price tags.
local function draw_amount(parts, n, x0, y, scale)
    for i = #parts, 1, -1 do parts[i]:Destroy() parts[i] = nil end
    local x = x0
    local remaining = n
    for _, tier in ipairs(TIERS) do
        local count = math.floor(remaining / tier.div)
        if count > 9 then count = 9 end
        remaining = remaining % tier.div
        for _ = 1, count do
            local s = tier.size * scale
            local p = Instance.new("Part")
            p.Name = "Coin"
            p.Size = Vector3.new(s, s, s)
            p.Color = tier.color
            p.Position = Vector3.new(x, y, 0)
            p.Anchored = true
            p.Parent = root
            parts[#parts + 1] = p
            x = x + s + 0.1 * scale
        end
        if count > 0 then x = x + 0.35 * scale end
    end
end

local coinParts, activeCostParts, passiveCostParts = {}, {}, {}

-- ---- actions ----------------------------------------------------------------------------------
local function mine()
    coins = coins + clickPower
    clickPop = 1        -- big pop
    flash = 1           -- white flash
end

-- Every upgrade block is always clickable. On an affordable click we run `purchase` and fire the green
-- success pop; on an unaffordable click we fire the red "can't afford" flash instead - so a click on a
-- gray block still gives feedback rather than silently doing nothing.
local function try_buy(btn, cost, purchase)
    if coins >= cost then
        purchase()
        btn.buyPop = 1
    else
        btn.reject = 1
    end
end
local function buy_active()
    try_buy(activeBtn, activeCost, function()
        coins = coins - activeCost
        clickPower = clickPower + 1
        activeCost = math.floor(activeCost * 1.6) + 1
        print("[clicker] click upgrade -> power " .. clickPower .. " (next costs " .. activeCost .. ")")
    end)
end
local function buy_passive()
    try_buy(passiveBtn, passiveCost, function()
        coins = coins - passiveCost
        passive = passive + 1
        passiveCost = math.floor(passiveCost * 1.7) + 1
        print("[clicker] passive upgrade -> " .. passive .. "/s (next costs " .. passiveCost .. ")")
    end)
end

-- ---- wire the clicks (real 3D picking: only the block under the cursor fires) ------------------
make_clickable(clicker, mine)
make_clickable(activeBtn.part, buy_active)
make_clickable(passiveBtn.part, buy_passive)

print("[clicker] loaded - CLICK THE GOLD BLOCK to mine. CLICK THE LEFT block to grow/upgrade your " ..
    "click, the RIGHT block for passive income. Green = affordable (clicking POPS green); clicking a " ..
    "gray block you cannot afford FLASHES it red.")

-- Animate one upgrade block: ease its idle/affordable base color, ease the buy/reject pulses back to 0,
-- then overlay red (reject) and near-white (buy) and a size pop so both outcomes read clearly.
local EARN_GREEN = Color3.fromRGB(140, 255, 150)
local function animate_button(btn, affordable, dt)
    btn.buyPop = btn.buyPop + (0 - btn.buyPop) * math.min(1, dt * 6)
    btn.reject = btn.reject + (0 - btn.reject) * math.min(1, dt * 7)
    btn.earn = btn.earn + (0 - btn.earn) * math.min(1, dt * 5)
    btn.grow = btn.grow + (btn.growTarget - btn.grow) * math.min(1, dt * 6)   -- ease toward the level size
    local target = affordable and AFFORD_COLOR or IDLE_COLOR
    btn.base = btn.base:Lerp(target, math.min(1, dt * 8))
    local col = btn.base:Lerp(REJECT_RED, btn.reject)          -- red flash on a can't-afford click
    col = col:Lerp(EARN_GREEN, btn.earn * 0.7)                 -- green pulse each time it earns
    col = col:Lerp(BUY_FLASH, btn.buyPop * 0.85)               -- bright pop on a purchase
    btn.part.Color = col
    -- persistent level size (grow) plus transient pops for buy / reject / earn
    local pop = btn.grow * (1 + btn.buyPop * 0.35 + btn.reject * 0.12 + btn.earn * 0.20)
    btn.part.Size = Vector3.new(BTN_BASE * pop, BTN_BASE * pop, 1)
end

RunService.Heartbeat:Connect(function(dt)
    if passive > 0 then
        passiveAccum = passiveAccum + passive * dt
        if passiveAccum >= 1 then
            local whole = math.floor(passiveAccum)
            coins = coins + whole
            passiveAccum = passiveAccum - whole
            passiveBtn.earn = 1        -- green pulse: the passive upgrade just earned coins
        end
    end
    -- the blocks grow with their level so a stronger upgrade is visibly bigger (capped so a grown block
    -- never reaches down over its price tag)
    passiveBtn.growTarget = 1 + math.min(passive, 6) * 0.10
    activeBtn.growTarget = 1 + math.min(clickPower - 1, 6) * 0.08

    -- clicker pop + white flash, both easing back
    clickPop = clickPop + (0 - clickPop) * math.min(1, dt * 9)
    flash = flash + (0 - flash) * math.min(1, dt * 6)
    local s = clicker_base_size() * (1 + clickPop * 0.45)
    clicker.Size = Vector3.new(s, s, s)
    clicker.Color = BASE_GOLD:Lerp(Color3.new(1, 1, 1), flash * 0.8)

    -- upgrade blocks: affordable glow + green buy pop + red can't-afford flash
    animate_button(activeBtn, coins >= activeCost, dt)
    animate_button(passiveBtn, coins >= passiveCost, dt)

    -- coin pile (rebuilt only when the whole-coin count changes)
    local whole = math.floor(coins)
    if whole ~= shownCoins then shownCoins = whole draw_amount(coinParts, whole, -8, 9, 1) end
    -- per-button price tags, drawn small under each button
    if activeCost ~= shownActive then shownActive = activeCost draw_amount(activeCostParts, activeCost, -9, -1.7, 0.5) end
    if passiveCost ~= shownPassive then shownPassive = passiveCost draw_amount(passiveCostParts, passiveCost, 7, -1.7, 0.5) end
end)
