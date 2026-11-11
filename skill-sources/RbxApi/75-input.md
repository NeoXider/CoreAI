# UserInputService, gameProcessed, ClickDetector, a keyboard game loop

Roblox-1:1 keyboard/mouse. Get it with `game:GetService("UserInputService")`, or use the global
`UserInputService` (same instance). Reading input is Read-tier — no WorldEdit needed.

- Events (fire `(input, gameProcessedEvent)`): `InputBegan`, `InputEnded`, `InputChanged`.
  `input` is an InputObject with `.KeyCode` (Enum.KeyCode), `.UserInputType` (Enum.UserInputType),
  `.UserInputState` (Enum.UserInputState), `.Position` (Vector3), `.Delta` (Vector3).
  `:Connect(fn)` and `:Once(fn)` return an RBXScriptConnection with `.Connected` and
  `:Disconnect()`; `:Wait()` yields until the next fire and returns its arguments.
- Polls: `IsKeyDown(Enum.KeyCode)` -> bool; `GetKeysPressed()` -> array of InputObject;
  `GetMouseLocation()` -> Vector2 (top-left origin).
- `MouseBehavior` (read+write, Enum.MouseBehavior: Default/LockCenter/LockCurrentPosition) locks
  the host cursor (LockCurrentPosition recentres it). `GetMouseDelta()` -> Vector2 pixels moved last
  frame, zero unless the pointer is locked, scaled by `MouseDeltaSensitivity` (read+write, 0..10,
  default 1). The mouse wheel fires `InputChanged` with UserInputType MouseWheel and the direction in
  `input.Position.Z` (+1 forward, -1 back). The gamepad sticks fire `InputChanged` with KeyCode
  Thumbstick1/Thumbstick2 and the stick in `input.Position` (x, y in -1..1, y up).
- `TextBoxFocused` / `TextBoxFocusReleased` fire when CoreAI's chat or Hub field takes / returns the
  keyboard (argument nil); a GUI TextBox does not raise them yet and `GetFocusedTextBox()` returns nil
  (use the TextBox's own `Focused` / `FocusLost`, section gui).
- `gameProcessedEvent` (the second argument of InputBegan/InputEnded/InputChanged) is `true` while
  CoreAI UI (the chat, the Hub) or your own UI (a TextBox, a UXML field) has text focus, and for mouse
  input while the pointer is over a button, a TextBox, an `Active` GUI object or a UXML element:
  start every handler with `if gameProcessed then return end`.
- `game:GetService('StarterPlayer')`: `DevComputerMovementMode` (Enum.DevComputerMovementMode,
  default UserChoice), `CameraMode` (Enum.CameraMode), `CameraMaxZoomDistance` (400),
  `CameraMinZoomDistance` (0.5); writes need WorldEdit. Each Player copies the three camera settings
  when it joins: `player.CameraMode`, `player.CameraMaxZoomDistance`, `player.CameraMinZoomDistance`
  (read+write) are what the camera uses.
- `ClickDetector` — parent one under a Part to make that 3D part clickable:
  `local cd = Instance.new("ClickDetector"); cd.Parent = part; cd.MouseClick:Connect(function() ... end)`.
  MouseClick fires `(playerWhoClicked)` when the user clicks THAT part with the mouse and the
  player's character is within `cd.MaxActivationDistance` studs of it (default 32; measured from
  the camera when there is no character). A detector under a Model or Folder covers all of its
  parts; the deepest detector above the clicked part wins. Only the part under the cursor fires.

Drive a part with the keyboard and follow it with the camera (a tiny mini-game loop):
```lua
local UIS = game:GetService("UserInputService")
local car = Instance.new("Part")
car.Size = Vector3.new(4, 1, 8)
car.Position = Vector3.new(0, 1, 0)
car.Anchored = true
car.Parent = workspace
workspace.CurrentCamera.CameraSubject = car             -- the default camera orbits the part

-- The game loop is RunService.Heartbeat — the Roblox per-frame hook. dt is the frame delta in
-- seconds; multiply speeds by dt so motion is smooth and frame-rate independent (studs per second).
local RunService = game:GetService("RunService")
local SPEED = 12                                         -- studs per second
RunService.Heartbeat:Connect(function(dt)
  local move = Vector3.zero
  if UIS:IsKeyDown(Enum.KeyCode.W) then move = move + Vector3.new(0, 0, -1) end
  if UIS:IsKeyDown(Enum.KeyCode.S) then move = move + Vector3.new(0, 0, 1) end
  if UIS:IsKeyDown(Enum.KeyCode.A) then move = move + Vector3.new(-1, 0, 0) end
  if UIS:IsKeyDown(Enum.KeyCode.D) then move = move + Vector3.new(1, 0, 0) end
  car.Position = car.Position + move * SPEED * dt
end)

UIS.InputBegan:Connect(function(input, gameProcessed)
  if gameProcessed then return end                       -- typing in chat or a UI field
  if input.KeyCode == Enum.KeyCode.Space then
    car.Color = Color3.fromRGB(255, 80, 80)               -- flash on the jump key
  end
end)
```
