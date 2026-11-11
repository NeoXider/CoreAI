# Rbx API skill: the UI section (for root to merge)

Status: ready to merge, written 2026-11-11 with the P18 Lua surface. The skill pair
(`Assets/CoreAiUnity/Resources/AgentSkills/RbxApi.txt` and `BuiltInRbxApiSkillText.cs`) was being edited by
another agent, so the section lives here. Merge it as a new numbered section after "10. Input
(UserInputService)" (renumber the rest), and update three lines elsewhere in the skill:

- the `StarterGui` stub's workaround ("build HUD content in 3D space until the GUI subset lands") now
  points at `UxmlService` for HUDs;
- the capability list wherever the tiers are named gains `Ui` (part of `All`);
- "13. Errors" may list the `UI_*` codes as inner codes (`BAD_ARGUMENT: UI_CAPABILITY_REQUIRED: ...`).

The example is the fixture `Assets/CoreAIMods/Tests/EditMode/RbxApi/RuntimeUi/RuntimeUiHotbarFixture.cs`
(plus a `warn` loop over the reports): keep the two in step, because the fixture is what proves the interpreter
accepts it with zero reports (`RuntimeUiHotbarFixtureEditModeTests`) and that it works against the real host
(`UxmlServiceRuntimeUiHostEditModeTests`).

Everything between the two rules below is the section text.

---

## 11. UI (UxmlService) — CoreAI-only, needs the Ui capability

Interfaces are UXML + USS **text** handed to `game:GetService("UxmlService")` while the game runs. This is
CoreAI-only (Roblox has no markup; ScreenGui/Frame/TextLabel are not built yet). `Ui` is part of `All`; a
header that lists tiers must add `Ui`. One-off `execute_lua` chunks cannot own screens: create UI from a
mod.

- `ui:CreateScreen(id, uxml, uss?, {SortOrder = n, Visible = b}?)` -> UiScreen. Calling it again with the
  same id REPLACES that screen (values of named elements survive) — safe to re-run on every reload.
  `ui:GetScreen(id)`, `ui:GetScreens()`: your own screens only (ids are private to your mod).
- UiScreen: `Visible`, `SortOrder` (writable), `Show()`, `Hide()`, `SetUxml(text)`, `SetUss(text)` (both
  return the reports), `Diagnostics`, `Query(sel)`, `QueryAll(sel)`, `GetElement(name)`, `Root`,
  `CreateElement(kind, parent?, name?)`, `Destroy()`, `Destroying`.
- UiElement: `Text`, `Value` (Toggle boolean; Slider/ProgressBar/ListView number; TextField text),
  `Visible`, `Enabled`, `ZOrder` (all writable); `Name`, `Kind`, `Parent`, `IsAlive`;
  `AddClass/RemoveClass/ToggleClass/HasClass/GetClasses`; `SetStyle(prop, value)` -> bool,
  `ClearStyle(prop)`, `GetStyle(prop)`; `SetAttribute(name, value)`; `SetItems({strings})` (ListView);
  `Focus()`, `Blur()`, `Query(sel)`, `CreateChild(kind, name?)`, `Remove()`.
- Signals (ordinary `:Connect`, deferred, dropped on unload): `Clicked()` (buttons also on Enter/gamepad),
  `ValueChanged(newValue)`, `Submitted(text)` (Enter in a single-line TextField).
  `ui.TextInputFocused`, `ui.PointerOverUi`, `ui.TextInputFocusChanged(focused)`.
- Tokens: `ui:SetToken("--cai-color-accent", "rgb(255, 180, 0)")` restyles YOUR screens only;
  `ui:GetToken(name)`. Theme tokens: `--cai-color-bg/-surface/-surface-raised/-border/-text/-text-muted/
  -accent/-accent-hover/-on-accent`, `--cai-space-1..4`, `--cai-radius`, `--cai-font-size-small/-body/-h1`.
  Theme classes: `cai-panel cai-row cai-column cai-h1 cai-text cai-small cai-muted cai-btn
  cai-btn-primary is-hidden is-selected is-disabled`.
- Elements: `VisualElement Label Button Toggle Slider TextField ProgressBar Image ScrollView ListView`
  (an unknown one becomes a reported placeholder; templates cannot be registered from Lua yet). Write
  `ui:` prefixes with `<ui:UXML xmlns:ui="UnityEngine.UIElements">` (or none). Common attributes:
  `name class style tooltip picking-mode focusable enabled`. Give decorative overlays (a crosshair)
  `picking-mode="Ignore"`, or the pointer counts as "over UI".
- USS subset: selectors type, `.class`, `#name`, descendant, `>` child, `:hover :active :focus :disabled
  :checked :root`; `var(--x)`; properties for flex layout (`display position flex-* align-* justify-content
  left top right bottom width height min-/max-* overflow`), box (`margin padding border-width`), border
  (`border-color border-radius`), text (`color font-size -unity-font-style -unity-text-align white-space
  letter-spacing -unity-text-outline text-shadow`), `background-color background-image opacity visibility
  translate scale rotate transform-origin transition`. Lengths `px`/`%`. NOT supported (reported, ignored):
  `@media`/`@keyframes`, `+ ~` and attribute selectors, `:not()`/`:first-child`, `gap`, `z-index` (use
  `ZOrder`), `border`/`font` shorthands, `box-shadow`, `cursor`.
- Reports are RETURNED, never raised: read `screen.Diagnostics` (`{Code, Severity, Source, Line, Column,
  Message}`) and fix the line. Raised errors carry a UI code: `UI_CAPABILITY_REQUIRED`, `UI_UNAVAILABLE`
  (no UI on this host: headless/server), `UI_DISABLED`, `UI_MOD_REQUIRED`, `UI_NOT_OWNER`,
  `UI_ELEMENT_DETACHED` (look the element up again; name what you keep), `UI_INVALID_SELECTOR`,
  `UI_UNSUPPORTED_OPERATION` (e.g. `Value` on a Label), `UI_INVALID_ARGUMENT`, and the per-mod budgets
  `UI_BUDGET_SCREENS` (32 screens), `UI_BUDGET_ELEMENTS` (4096 elements), `UI_TEXT_TOO_LARGE` (64 KB per
  call), `UI_BUDGET_WRITES` (8192 element writes per frame — write only what changed).
- Input over UI arrives with `gameProcessed = true`: keep `if gameProcessed then return end` in input
  handlers so typing in a field or clicking a slot does not reach the game.

Hotbar, crosshair and block counter:
```lua
local UxmlService = game:GetService("UxmlService")
local UserInputService = game:GetService("UserInputService")

local UXML = [[
<ui:UXML xmlns:ui="UnityEngine.UIElements">
  <ui:Label name="crosshair" class="crosshair" text="+" picking-mode="Ignore" />
  <ui:VisualElement name="hotbar" class="hotbar">
    <ui:Button name="slot1" class="slot" text="Stone" />
    <ui:Button name="slot2" class="slot" text="Dirt" />
    <ui:Button name="slot3" class="slot" text="Wood" />
    <ui:Button name="slot4" class="slot" text="Glass" />
    <ui:Button name="slot5" class="slot" text="Brick" />
  </ui:VisualElement>
  <ui:Label name="counter" class="counter" text="Blocks: 0" />
</ui:UXML>
]]

local USS = [[
.hotbar { position: absolute; bottom: 16px; left: 50%; translate: -50% 0; flex-direction: row; }
.slot { width: 64px; height: 64px; margin: 4px; border-width: 2px; border-color: var(--cai-color-border);
        background-color: var(--cai-color-surface); color: var(--cai-color-text); }
.slot:hover { scale: 1.05; transition: scale 0.1s ease-out; }
.slot.is-selected { border-color: var(--cai-color-accent); border-width: 4px; }
.crosshair { position: absolute; left: 50%; top: 50%; translate: -50% -50%; font-size: 28px; color: white; }
.counter { position: absolute; top: 16px; right: 16px; font-size: 20px; color: var(--cai-color-text); }
]]

local hud = UxmlService:CreateScreen("hud", UXML, USS, { SortOrder = 10 })
for _, report in ipairs(hud.Diagnostics) do
  warn(report.Code .. " line " .. report.Line .. ": " .. report.Message)
end

local slots = hud:QueryAll("#hotbar > .slot")
local counter = hud:GetElement("counter")
local placed = 0

local function selectSlot(index)
  for i, slot in ipairs(slots) do
    if i == index then slot:AddClass("is-selected") else slot:RemoveClass("is-selected") end
  end
end

for i, slot in ipairs(slots) do
  slot.Clicked:Connect(function() selectSlot(i) end)
end

local keys = { Enum.KeyCode.One, Enum.KeyCode.Two, Enum.KeyCode.Three, Enum.KeyCode.Four, Enum.KeyCode.Five }
UserInputService.InputBegan:Connect(function(input, gameProcessed)
  if gameProcessed then return end
  if input.UserInputType == Enum.UserInputType.MouseButton1 then
    placed = placed + 1
    counter.Text = "Blocks: " .. placed
    return
  end
  for i, key in ipairs(keys) do
    if input.KeyCode == key then selectSlot(i) end
  end
end)

selectSlot(1)
```

---
