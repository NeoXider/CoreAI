# Finders, Changed signals, Clone rules, Debris, Model pivots, the service catalog

- Finders: `FindFirstChild(name[,recursive])`, `FindFirstChildOfClass(cls)`,
  `FindFirstChildWhichIsA(cls[,recursive])`, `FindFirstAncestor(name)`,
  `FindFirstAncestorOfClass(cls)`, `FindFirstAncestorWhichIsA(cls)`, `GetChildren()`,
  `GetDescendants()`, `GetFullName()`, `IsA(cls)`, `IsDescendantOf(x)` (nil is BAD_ARGUMENT),
  `IsAncestorOf(x)`. `inst.ChildName` is sugar for an existing child; a missing member errors (use
  FindFirstChild). `WaitForChild(name)` for an absent child yields through the scheduler bridge.
- Properties: Name, ClassName (read-only), Parent, Archivable. Setting Name/Parent/Archivable
  needs WorldEdit. Name keeps its first 100 characters. A Parent deeper than 2,048 levels is
  BAD_ARGUMENT. A Player cannot be destroyed or re-parented (use `player:Kick()`).
- `Instance.new("Class", parent)`: the parent argument is deprecated and logs once; set `.Parent` after
  configuring. The instance budget error names the call that went over it.
- `inst.Changed(propertyName)` fires on every instance (a value object passes the new Value);
  `inst:GetPropertyChangedSignal("Name")` fires with no args. It refuses an event or method name
  and a typo of a known property (wrong case or one letter off: "position" -> "did you mean
  Position"); a real Roblox property CoreAI does not model yet gets a signal that never fires (logged
  once; the same signal each time you ask for that name, and `Destroy()` disconnects it). Writes by
  a script, a tween or PivotTo fire them (derived members too: a CFrame write fires
  Position/Orientation/Rotation); an equal assignment fires nothing and physics movement never
  does. `AncestryChanged(movedInstance, newParent)` fires on every descendant.
  ChildRemoved/DescendantRemoving and a tag's removed-signal handlers fired by `Destroy()` can
  still read the destroyed instance they were handed (e.g. its Name), as Destroying handlers can.
  `game:IsLoaded()` is true; `game.Loaded` never fires.
- Clone is a deep
  copy: name, attributes, tags and (for parts) Size/CFrame/Color/Anchored/Shape carry across;
  non-Archivable subtrees are skipped and the copy gets fresh ids; `PrimaryPart` and
  `ObjectValue.Value` pointing inside the copy point at the copies, and WorldPivot is kept.
  `game:Clone()`, a service's Clone and `player:Clone()` return nil; `game:Destroy()` errors.
- `Debris:AddItem(inst[, seconds])` (default 10 s) schedules guaranteed destruction without
  yielding and survives the scheduling script's death — the idiom for temporary
  projectiles/effects. WRONG: `task.delay(10, function() part:Destroy() end)` (dies with the
  script); RIGHT: `game:GetService("Debris"):AddItem(part, 10)`. Past 1,000 queued items the
  oldest is destroyed instantly, so treat the lifetime as a maximum, not an exact delay.
  Needs WorldEdit and write access to the whole subtree; services, `game`, the CurrentCamera and a
  Player are refused (a Player leaves through `player:Kick()`). Re-adding an item replaces its
  lifetime but keeps its place in the eviction order.
- The service catalog has 43 registrations. `HttpService`, `Players`, `Debris`, `TweenService`,
  `CollectionService` and `ScriptContext` are tree-backed (NOT placeholders); `RunService` and
  `UserInputService` have catalog fallbacks that the standard runtime replaces with its live
  implementations (a bare fallback's error names the missing attachment). The standard runtime has
  live tree-backed `Workspace`, `Lighting`, `ReplicatedStorage`, `ServerStorage`,
  `ServerScriptService`, `StarterPlayer`, `StarterGui`, `Players`, `HttpService`, `UserInputService`,
  `RunService`, `MaterialService`, `Debris`, `TweenService` and `CollectionService`, plus
  `UxmlService` (section uxml), `TextChatService` (its `TextChatCommand`s, section chat) and `AIService`
  (answers only when the game turned on AI from Lua, section ai) and `DataStoreService` (section data).
  Placeholders: `ContextActionService` (MVP7),
  `SoundService` (MVP17), and backlog or unsupported Roblox services such as `Teams`,
  `PhysicsService`, `ReplicatedFirst`, `PathfindingService`, `MarketplaceService`, `TeleportService`
  and `BadgeService` (each error says which and gives a workaround). A placeholder's first member
  read, write, or method lookup raises NOT_IMPLEMENTED and names that service's rung, so top-of-file
  acquisition does not abort setup; do not add defensive workarounds solely to avoid that obsolete
  failure. An unknown name raises UNKNOWN_SERVICE with "X is not a valid Service name". Also
  `game:FindService(name)`. `workspace` is `game:GetService("Workspace")`.

## Model pivots

`model.PrimaryPart` is a BasePart or nil; `model.WorldPivot` is a writable CFrame.
`model:GetPivot()` uses the PrimaryPart CFrame when set, otherwise WorldPivot (initially the
contents' bounding-box center). `model:PivotTo(cf)` rigidly moves every descendant PVInstance.
Pivot/property writes need WorldEdit. BaseParts also support `GetPivot()` and `PivotTo(cf)`
(which moves the part's descendants too), and so does `workspace.CurrentCamera` (a PVInstance).

## Error messages

```lua
local ok, err = pcall(function() return Instance.new("Banana") end)
if not ok then print(err) end   -- "BAD_ARGUMENT: Unable to create ... | fix: ... Part ..."

local part = Instance.new("Part"); part.Parent = workspace
part:Destroy()
local ok2, err2 = pcall(function() return part.Position end)
if not ok2 then print(err2) end  -- "INSTANCE_DESTROYED: ... | fix: ..."
```
