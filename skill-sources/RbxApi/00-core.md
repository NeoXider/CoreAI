# Rbx API core: the rules every Roblox-style script needs

Roblox-style Lua inside CoreAI mods (load code with manage_mods / execute_lua); it mixes freely with
the classic globals (hooks_every, store_set, coreai_world_*: read_skill('Lua Modding')). Before you use a
feature this core names a section for, read that section; read only the sections your task uses.

<!-- sections -->

## Rules
- Coordinates are in STUDS, not meters (1 stud = 0.28 m by default). Right-handed axes; a CFrame's
  LookVector is -Z. The engine maps studs->Unity for you; a mod NEVER converts units or flips Z itself.
- One shared world: `game` (DataModel) and `workspace` are globals every mod and every execute_lua call
  see: an instance one mod creates is the one another mod finds.
- Creating or mutating instances needs the WorldEdit capability (persistent mods have it by default);
  read-only scripts can read the world, input and the camera, and `Instance.new` raises there.
- Signals are DEFERRED: handlers run at the next script-resumption point, not where the signal fires,
  and the order of several connections is NOT guaranteed. A handler may `task.wait()`. A one-off
  execute_lua chunk has no owning mod: Connect/Once/ConnectParallel/Wait there raise CONTEXT_VIOLATION,
  so put listeners, UI and AI agents in a mod.
- Budgets are per resume: loop forever only if the loop yields (`while true do task.wait() end`). A
  resume that never yields, or keeps too much memory alive, is cut with BUDGET_EXCEEDED, and the cut
  CANNOT be caught (`pcall`/`xpcall` let it through): fix the loop, do not wrap it.
- Drive a game from one `RunService.Heartbeat:Connect(function(dt) ... end)` and scale motion by `dt`.
  Anchor gameplay parts (`part.Anchored = true`) unless you WANT physics: an unanchored Part falls under
  gravity and a dense stack shoves itself apart.

## Instances and parts
- `Instance.new("Class")`, configure, then set `.Parent` last (the parent argument is deprecated).
  Creatable: Part, Folder, Model, Backpack (a plain container), ClickDetector (section input),
  RemoteEvent, UnreliableRemoteEvent, RemoteFunction, BindableEvent, BindableFunction (messaging),
  MaterialVariant (parts), Humanoid (characters), PointLight (lighting), TextGenerator (ai), the value
  objects (attributes) and the GUI classes (gui). A real Roblox class CoreAI does not build yet
  (WedgePart, SpawnLocation, Weld, Attachment, ...) raises NOT_IMPLEMENTED; any other name errors. A
  Camera is not creatable: use `workspace.CurrentCamera`. `workspace.Terrain` exists in every world
  (terrain): build ground and many blocks with it, not one Part per block.
- The actor a mod runs for may own at most 2,048 instances (a WebGL world at most 4,032 in total): one
  more `Instance.new`, `Clone` or `TweenService:Create` raises BUDGET_EXCEEDED; `Destroy()` what you no
  longer need instead of retrying. Temporary objects: `Debris:AddItem(inst, seconds)` (instances).
- `FindFirstChild(name[,recursive])` returns nil on a miss; `inst.ChildName` errors on a miss.
  `WaitForChild(name)` yields until the child exists. Other finders: section instances.
- `Clone()`, `Destroy()`, `ClearAllChildren()` need WorldEdit. After Destroy any member access errors with
  INSTANCE_DESTROYED; re-parenting it raises PARENT_LOCKED: drop the reference, make a new one.
- Part properties, read+write: `Position`, `Size`, `CFrame`, `Color` (Color3), `Transparency` (0..1),
  `Anchored`, `CanCollide`, `Shape` (Enum.PartType), `Material` (Enum.Material), `MaterialVariant`,
  `Orientation` and `Rotation` (Vector3 of degrees; Orientation is YXZ, Rotation XYZ). Setting
  `Position` keeps the rotation; setting `CFrame` sets position AND rotation; setting `Orientation` or
  `Rotation` sets the rotation and keeps the position.

```lua
local p = workspace:FindFirstChild("Block1")
if p then
  p.CFrame = p.CFrame * CFrame.Angles(0, math.pi / 4, 0) -- rotate 45 deg about Y
  p.Position = p.Position + Vector3.new(0, 5, 0) -- 5 studs up, rotation kept
  print(tostring(p.Position)) -- "x, y, z" in studs
end
```

## Services, UI and AI
- `game:GetService("Name")`. Roblox services CoreAI has not built yet (DataStoreService, SoundService,
  ContextActionService, PathfindingService, Teams, ...) are placeholders: GetService returns them (do not
  guard it) and their first member use raises NOT_IMPLEMENTED with a workaround. An unknown name raises
  UNKNOWN_SERVICE. Which services are real: section instances.
- UI needs the `Ui` capability (part of `All`; a header that lists tiers must add `Ui`), lives in a mod
  and goes away with it: Roblox GUI (gui) or UXML screens (uxml). Input handlers start with
  `if gameProcessed then return end` (input). AI from Lua (ai) works only when the game turned it on.

## Not implemented (raise NOT_IMPLEMENTED: do not use)
`Instance.fromExisting` (use `Clone`), `model:MoveTo(pos)` (use `model:PivotTo(CFrame.new(pos))`), and
the globals `BrickColor` (use `Color3.fromRGB`), `NumberSequence`, `ColorSequence`, `NumberRange`, `Rect`,
`PhysicalProperties`, `OverlapParams`, `DateTime` (use `os.time()`) and `shared` (use
mods_export/mods_get). Each section lists its own stubs.

## Errors
Every failure is a Lua error `CODE: message | fix: suggestion`, inside a mod prefixed with
`[mod:<id> script:main.lua line:N] `. Argument numbers do not count `self`. `pcall`, `xpcall` and
`coroutine.resume` get exactly that one line (no stack trace). Codes: BAD_ARGUMENT, UNKNOWN_SERVICE,
INSTANCE_DESTROYED, PARENT_LOCKED, NOT_IMPLEMENTED, WORLD_DETACHED, BUDGET_EXCEEDED, SIGNAL_CASCADE,
CONTEXT_VIOLATION, NOT_AUTHORITY, THREAD_CAP, PAYLOAD_TOO_LARGE; a UI refusal carries an inner code
(`BAD_ARGUMENT: UI_CAPABILITY_REQUIRED: ...`). WORLD_DETACHED is not your bug (the world was torn down:
scene load, domain reload, leaving play mode): report it, do not retry, until the mods are reloaded.
