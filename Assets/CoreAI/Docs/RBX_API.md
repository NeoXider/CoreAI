# Roblox-style Lua API (Rbx API)

**This is the API a mod uses to create and control world objects.** It mirrors Roblox 1:1, so a
script written here imports and exports without a rewrite. C# identifiers use the `Rbx` prefix
(`CoreAI.RbxApi.*`); the Lua-facing surface keeps the Roblox spelling exactly.

> The older low-level `coreai_world_spawn` / `coreai_world_change` / `coreai_world_destroy` build
> functions are **not registered in the default shipping composition** — `CoreAiModsInstaller` sets
> `RegisterWorldEditBuildBindings = false`, and calling one raises an actionable
> `LuaApiWithheldException` instead of silently doing nothing. Use this API instead. The read-only
> queries (`coreai_world_find` / `_pos` / `_exists`) are unaffected and stay available at the `Read`
> tier. See [LUA_GAME_API.md](LUA_GAME_API.md) for that legacy surface and the hosts that still opt in.

## Capability

`Instance` is registered only when the mod holds the **`WorldEdit`** capability. Every other global
below is available at `Read`. `camera_set_cframe` / `camera_follow` are registered on every tier but
throw the capability error when called without `WorldEdit`, so a mod never sees "attempt to call a
nil value".

## Globals

| Global | What it is |
|---|---|
| `Instance` | `Instance.new(className)` — the object constructor (needs `WorldEdit`) |
| `game` | The DataModel: `game:GetService(name)` |
| `workspace` | The world root; also `workspace.CurrentCamera` |
| `UserInputService` | Alias of `game:GetService("UserInputService")` |
| `Vector3`, `Vector2` | `.new(x, y, z)`, arithmetic, `.Magnitude`, `.Unit` |
| `CFrame` | `.new(...)`, `CFrame.lookAt(from, to)`, `*` composition |
| `Color3` | `.new(r, g, b)` (0..1), `Color3.fromRGB(r, g, b)` (0..255) |
| `UDim`, `UDim2` | Scale/offset pairs |
| `Random` | Seedable RNG |
| `Enum` | `Enum.CameraType.Scriptable`, `Enum.KeyCode.A`, … |
| `task` | `task.wait`, `task.spawn`, `task.delay` |
| `camera_set_cframe`, `camera_follow` | CoreAI convenience shorthands for camera control |

## Classes

`Instance.new` accepts: **`Part`**, **`Folder`**, **`Model`**, **`Camera`**, **`ClickDetector`**.
The class ancestry (`BasePart`, `PVInstance`, `WorldRoot`, …) is data-driven through `ClassCatalog`,
so `IsA` works the way it does in Roblox.

Services reachable via `game:GetService`: `RunService`, `UserInputService`, `Players`,
`CollectionService`, `TweenService`, `SoundService`, `Lighting`, `Debris`, `HttpService`,
`ReplicatedStorage`, `ServerStorage`, `ServerScriptService`, `ContextActionService`,
`PathfindingService`, `MarketplaceService`, `DataStoreService`, and CoreAI's own `AIService`.

### Part properties

`Name`, `Parent`, `Position`, `CFrame`, `Size`, `Color`, `Anchored`, `Transparency`, `CanCollide`,
and `Shape` (`Ball`, `Block`, `Cylinder`, `Wedge`, `CornerWedge`). A `Part` materializes as a real
GameObject with the URP Lit material as soon as its `Parent` is set into the world.

## A working mod

```lua
local RunService = game:GetService("RunService")
local uis = game:GetService("UserInputService")

-- Own everything under one Folder: destroying it removes the whole mod's world state.
local root = Instance.new("Folder")
root.Name = "MyGame"
root.Parent = workspace

local block = Instance.new("Part")
block.Name = "Player"
block.Size = Vector3.new(2, 1, 3)
block.Color = Color3.fromRGB(60, 140, 255)
block.Anchored = true            -- no physics; the script owns the position
block.Position = Vector3.new(0, 1, 0)
block.Parent = root

local cam = workspace.CurrentCamera
cam.CameraType = Enum.CameraType.Scriptable
cam.CFrame = CFrame.lookAt(Vector3.new(0, 9, 14), Vector3.new(0, 2, -18))

RunService.Heartbeat:Connect(function(dt)
    -- dt is seconds, so motion is frame-rate independent (speeds in studs/second).
    block.Position = block.Position + Vector3.new(0, 0, -8 * dt)
end)
```

Parent every object you create under one `Folder` you own. When the mod is disabled or unloaded the
runtime destroys the instances it created (tracked through the `OriginTag` ownership ledger), and a
single owned root makes that deterministic.

## Bundled sample mods

Four mods ship inside the Mods package at
`Assets/CoreAIMods/Runtime/Resources/CoreAIMods/` and are the reference for idiomatic usage:

| Mod | Ships | What it demonstrates |
|---|---|---|
| `sample_welcome` | **active** | Minimal mod: header manifest, `print`, a tick counter |
| `sample_lane_racer` | disabled | `RunService.Heartbeat`, `UserInputService` rising edges, scripted camera |
| `sample_tetris3d` | disabled | Grid logic in plain Lua tables, smooth part motion, restart |
| `sample_clicker` | disabled | `ClickDetector` 3D click-picking, no UI at all |

The three playable ones ship `active: false`; the player turns them on from the **Hub → Mods** tab.

## Platform support

The whole surface runs under **IL2CPP**, including WebGL at managed stripping level **Medium** —
verified on a WebGL player where the DI container builds, bundled mods seed, and mod-driven
`Instance.new` spawns visible parts. The stripping protection comes from the `link.xml` shipped in
the CoreAiUnity package; add your own assemblies to your project's `link.xml` if you resolve them
through DI or reflection.

## Related

- [LUA_GAME_API.md](LUA_GAME_API.md) — the mod runtime itself (hooks, store, events, cross-mod exports)
- [FIRST_MOD.md](FIRST_MOD.md) — writing and loading your first mod
- [LUA_ACCESS_MODES.md](LUA_ACCESS_MODES.md) — capability tiers and what each one opens
