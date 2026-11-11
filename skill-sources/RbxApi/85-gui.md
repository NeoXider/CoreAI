# Roblox GUI: ScreenGui, Frame, TextLabel, TextButton, PlayerGui

Two entrances draw on one screen layer: the Roblox GUI classes below and CoreAI's UXML screens (section
uxml). Both need the `Ui` capability and a mod (one-off `execute_lua` chunks cannot own UI), and they share
the budgets and `UI_*` error codes listed in section uxml. A ScreenGui's `DisplayOrder` and a screen's
`SortOrder` are one scale: higher draws on top.

Roblox GUI works as in Roblox: `ScreenGui`, `Frame`, `TextLabel`, `TextButton`, `TextBox`, `ImageLabel`,
`ImageButton`, `ScrollingFrame`, `UICorner`, `UIPadding`, `UIStroke`, `UIScale`, `UIListLayout`,
`UIGridLayout`, `UIAspectRatioConstraint`, `UISizeConstraint` and `StarterGui`, with Roblox's properties,
defaults, layout and signals; `AbsolutePosition`/`AbsoluteSize` are what is drawn, and TweenService tweens
their UDim2, Color3 and number properties. Where CoreAI differs:
- The PlayerGui drawn is this machine's player's. A mod loaded as that player's actor reaches it as
  `Players.LocalPlayer.PlayerGui`; a mod the host loads (LocalPlayer nil, section characters) uses
  `Players:GetPlayers()` / `PlayerAdded`, or puts its ScreenGui in `StarterGui`.
- `StarterGui` is copied into PlayerGui when the character spawns (`ResetOnSpawn = false`: copied once and
  kept). A child added after the spawn is copied on the next frame; a respawn replaces only the copies, so a
  ScreenGui you parented into PlayerGui yourself stays; destroying an original removes its copies; scripts
  are never copied. Of the core-GUI switches (`SetCoreGuiEnabled`) only `PlayerList` draws something (the
  leaderstats list, section data); the others are recorded, and there is no top bar (`IgnoreGuiInset`
  changes nothing).
- Every font draws in the host font, bold or italic by the font's name. `TextScaled` estimates the size and
  `AutomaticSize` fits children, not text (text is not measured; `TextBounds`/`TextFits` raise
  NOT_IMPLEMENTED). `RichText` draws `<b> <i> <u> <s>`, not Roblox's `<font ...>` or `<stroke>`.
- `Image` must be a URL or path the host can load: `rbxassetid://` ids are never downloaded and draw
  nothing. Nine-slice, `UIGradient`, `HoverImage`/`PressedImage` and `ZIndexBehavior.Global` (drawn as
  Sibling) are not drawn as in Roblox.
- A `ScrollingFrame` scrolls as in Roblox: the wheel, a touch drag and its bar move the canvas within
  `CanvasSize` (for a list set `CanvasSize = UDim2.new()` and `AutomaticCanvasSize = Enum.AutomaticSize.Y`),
  and `CanvasPosition` follows (its signal fires; the player's scrolling costs no write budget, a script's
  write does). `ScrollingEnabled = false` and `ScrollingDirection` stop the player, never a script.
  `CanvasPosition` stays within 0 and `AbsoluteCanvasSize - AbsoluteWindowSize`. The bar is flat, in
  `ScrollBarImageColor3`/`ScrollBarImageTransparency` (`ScrollBarThickness`, the two insets and
  `VerticalScrollBarPosition` work); `ElasticBehavior` and the bar images raise NOT_IMPLEMENTED.
- `InputBegan/InputChanged/InputEnded`, `MouseButton1Down/Up`, the `MouseButton2*` and Touch signals and
  `TweenPosition/TweenSize` raise NOT_IMPLEMENTED (planned MVP15); `BillboardGui`, `SurfaceGui`,
  `ViewportFrame` and `CanvasGroup` are backlog.
- A GUI object you listen to `MouseEnter`/`MouseLeave` on takes the pointer, so input over it arrives
  `gameProcessed` as over an `Active` one.
- A ScreenGui counts as a screen, each GUI object as an element, a property write of a shown object as an
  element write.
