namespace CoreAI.Tests.EditMode.RbxApi.RuntimeUi
{
    /// <summary>
    /// The hotbar example of the Rbx API skill's UI section as a test fixture: a crosshair, a hotbar of
    /// five block slots and a block counter, all made by one Lua mod from UXML and USS text. Slot
    /// clicks and the number keys 1-5 select a slot; a left click into the world counts a placed block,
    /// and input the UI took (<c>gameProcessed</c>) is ignored. The
    /// same source runs against the fake backend (portable) and the real runtime UI host (EditMode).
    /// </summary>
    internal static class RuntimeUiHotbarFixture
    {
        /// <summary>The mod id the fixture is loaded under.</summary>
        public const string ModId = "hotbar";

        /// <summary>The host id of the fixture's screen (the mod id namespaces the mod-local id).</summary>
        public const string HostScreenId = ModId + "/hud";

        /// <summary>The slot element names in hotbar order.</summary>
        public static readonly string[] SlotNames = { "slot1", "slot2", "slot3", "slot4", "slot5" };

        /// <summary>The hotbar markup (also checked against the interpreter on its own).</summary>
        public const string Uxml = @"<ui:UXML xmlns:ui=""UnityEngine.UIElements"">
  <ui:Label name=""crosshair"" class=""crosshair"" text=""+"" picking-mode=""Ignore"" />
  <ui:VisualElement name=""hotbar"" class=""hotbar"">
    <ui:Button name=""slot1"" class=""slot"" text=""Stone"" />
    <ui:Button name=""slot2"" class=""slot"" text=""Dirt"" />
    <ui:Button name=""slot3"" class=""slot"" text=""Wood"" />
    <ui:Button name=""slot4"" class=""slot"" text=""Glass"" />
    <ui:Button name=""slot5"" class=""slot"" text=""Brick"" />
  </ui:VisualElement>
  <ui:Label name=""counter"" class=""counter"" text=""Blocks: 0"" />
</ui:UXML>";

        /// <summary>The hotbar style sheet (also checked against the interpreter on its own).</summary>
        public const string Uss = @".hotbar { position: absolute; bottom: 16px; left: 50%; translate: -50% 0; flex-direction: row; }
.slot { width: 64px; height: 64px; margin: 4px; border-width: 2px; border-color: var(--cai-color-border);
        background-color: var(--cai-color-surface); color: var(--cai-color-text); }
.slot:hover { scale: 1.05; transition: scale 0.1s ease-out; }
.slot.is-selected { border-color: var(--cai-color-accent); border-width: 4px; }
.crosshair { position: absolute; left: 50%; top: 50%; translate: -50% -50%; font-size: 28px; color: white; }
.counter { position: absolute; top: 16px; right: 16px; font-size: 20px; color: var(--cai-color-text); }";

        /// <summary>The mod source.</summary>
        public const string Source = @"
local UxmlService = game:GetService('UxmlService')
local UserInputService = game:GetService('UserInputService')

local UXML = [[
" + Uxml + @"
]]

local USS = [[
" + Uss + @"
]]

local hud = UxmlService:CreateScreen('hud', UXML, USS, { SortOrder = 10 })
local slots = hud:QueryAll('#hotbar > .slot')
local counter = hud:GetElement('counter')
local placed = 0

local function selectSlot(index)
  for i, slot in ipairs(slots) do
    if i == index then
      slot:AddClass('is-selected')
    else
      slot:RemoveClass('is-selected')
    end
  end
end

for i, slot in ipairs(slots) do
  slot.Clicked:Connect(function()
    selectSlot(i)
  end)
end

local keys = { Enum.KeyCode.One, Enum.KeyCode.Two, Enum.KeyCode.Three, Enum.KeyCode.Four, Enum.KeyCode.Five }
UserInputService.InputBegan:Connect(function(input, gameProcessed)
  if gameProcessed then
    return
  end
  if input.UserInputType == Enum.UserInputType.MouseButton1 then
    placed = placed + 1
    counter.Text = 'Blocks: ' .. placed
    return
  end
  for i, key in ipairs(keys) do
    if input.KeyCode == key then
      selectSlot(i)
    end
  end
end)

selectSlot(1)
";

        /// <summary>Roblox <c>Enum.KeyCode.One</c> (the key code value of '1').</summary>
        public const int KeyCodeOne = 49;

        /// <summary>Roblox <c>Enum.KeyCode.Three</c> (the key code value of '3').</summary>
        public const int KeyCodeThree = 51;
    }
}
