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

`Instance.new` accepts: **`Part`**, **`Folder`**, **`Model`**, **`ClickDetector`**,
**`RemoteEvent`**, **`UnreliableRemoteEvent`**, and **`RemoteFunction`**. `Camera` is not
creatable — the world's one camera is `workspace.CurrentCamera`.
The class ancestry (`BasePart`, `PVInstance`, `WorldRoot`, …) is data-driven through `ClassCatalog`,
so `IsA` works the way it does in Roblox.

Services reachable via `game:GetService`: `RunService`, `UserInputService`, `Players`,
`CollectionService`, `TweenService`, `SoundService`, `Lighting`, `Debris`, `HttpService`,
`ReplicatedStorage`, `ServerStorage`, `ServerScriptService`, `ContextActionService`,
`PathfindingService`, `MarketplaceService`, `DataStoreService`, and CoreAI's own `AIService`.

### Part properties

`Name`, `Parent`, `Position`, `CFrame`, `Size`, `Color`, `Material`, `Orientation`, `Rotation`,
`Anchored`, `Transparency`, `CanCollide`, and `Shape` (`Ball`, `Block`, `Cylinder`, `Wedge`,
`CornerWedge` — each one materializes its own mesh). A `Part` materializes as a real GameObject as
soon as its `Parent` is set into the world.

`Position` keeps the part's rotation; `CFrame` sets position and rotation together; `Orientation`
(YXZ degrees) and `Rotation` (XYZ degrees) set the rotation and keep the position.

### `BasePart.Material` and `Part.Color`

`Material` takes an `Enum.Material` item and **every one of the 45 enum items renders**. Six of them
— `Brick`, `Wood`, `WoodPlanks`, `Grass`, `Cobblestone`, `Metal` — resolve CC0 textured PBR surfaces
through `RbxTextureMaterialProvider`; the other 39 are procedural surfaces from
`RbxProceduralMaterialProvider` (opaque, metallic, organic, transparent, neon, and force-field shader
paths). A material id that is not in the catalog resolves to an opaque **magenta diagnostic
material** instead of failing quietly, so a wrong value is visible on the first frame.

```lua
local slab = Instance.new("Part")
slab.Material = Enum.Material.Cobblestone   -- textured PBR surface
slab.Anchored = true
slab.Parent = workspace
```

`Color` stays an independent Roblox-style **tint**, exactly as in Roblox. A part whose `Color` was
never assigned renders the material's own albedo (the stored Roblox default stays medium stone
grey); assigning `Color` marks it explicit and modulates the material's albedo from then on. The
tint rides a `MaterialPropertyBlock`, so parts sharing a material never clone it. `Neon` is the one
material with no palette of its own: its emission *is* `Part.Color`, so a default grey part glows
grey and a red one glows red — the same as Roblox.

Catalog detail lives in
[`PROCEDURAL_MATERIALS.md`](../../CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md) and
[`TEXTURE_MATERIALS.md`](../../CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md).

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

## RemoteFunction timeout compatibility deviation

Roblox documents `RemoteFunction` invocation as yielding until the recipient responds, and explicitly
warns that an `InvokeClient` recipient which never returns can leave the sender yielded forever. CoreAI
intentionally bounds both `InvokeServer` and `InvokeClient` to **30 scheduler seconds**. A missing or
stalled receiver raises an error in this form:

```text
RemoteFunction invoke refused actor '<actor-id>' for remote '<full-name>': response timed out after 30 seconds
```

This is an explicit compatibility deviation for runtime liveness, especially in a single-threaded WebGL
player where an unbounded loopback request would otherwise leave the Lua caller permanently suspended.
Late responses are ignored. Use `RemoteEvent` when no response is required, and wrap a fallible invocation
in `pcall` when the mod can recover.

## Saving and loading a world

A world is one `.world` ZIP container (`manifest.json` + `world.json` + indexed `Mods/NNNN/`
entries) holding the **world-owned** instance tree, world settings including `meters_per_stud`,
optional camera state, and the exact Lua mod sources. Mod-ephemeral subtrees (anything under a node
with an `OwnerModId`) are deliberately excluded: mods restart clean and recreate their own objects
after a load.

Two AI-facing tools live on that format:

| Tool | What it does |
|---|---|
| `save_world` | Writes a **create-once** manual slot. It never overwrites or deletes an existing slot. |
| `load_world` | Cannot apply a package. It only returns `player_confirmation_required` plus a one-use request id. |

The load flow is deliberately fail-closed: host or UI code subscribes to
`ManualLoadConfirmationRequested` (or reads `GetPendingManualLoads`) and calls
`ConfirmManualLoadAsync(requestId, true|false)`. The built-player **Hub → World Loads** page renders
those pending requests and is the surface where the player accepts or rejects one. Requests expire
after two minutes by default, a newer request for the same slot replaces the older one, and expired,
unknown, rejected, or reused ids never touch the live session.

**Autosaves are separate and automatic.** `ConfirmedWorldMutationGate` sits in front of every
`execute_lua` call (trigger `execute_lua`) and every *mutating* `manage_mods` action — `load`,
`reload`, `unload`, `import`, `forget`, `revert` (trigger `manage_mods-<action>`). It captures the
world and writes an autosave *before* the mutation runs; if the capture or the write fails, the
mutation does not happen and the tool returns a structured failure. Read-only `manage_mods` actions
(`list`, `get_source`, `export`, `versions`, `diagnostics`) bypass the gate entirely. Autosaves
rotate in a ring with a two-phase durability protocol; manual slots are never rotated or rewritten
by the gate.

A confirmed load replaces the runtime session rather than patching it: sources are written to an
isolated version directory and flushed to durable storage first, then a fresh registry, Rbx binding
layer, and Lua stack are staged and published atomically. `ILuaModRuntime`, `LuaCsModStack`,
`LuaCsLogicSlots`, and `ILuaModSourceStore` are stable facades, so a held reference keeps working
across the swap. An active mod that requests the `Full` capability is rejected before staging,
because arbitrary Unity reflection cannot be transactionally isolated.

On WebGL the durability boundary is `CoreAiWebGlPersistence.SyncAsync()`, which completes only from
the matching `FS.syncfs` callback (a cancellation or 30-second timeout drops the pending call and a
late callback is ignored). The browser player also refuses packages above 4 MiB, more than 4,096
instances, more than 32,768 collection items, or more than 2 MiB of text before entering unbounded
work — those are WebGL execution limits, not format limits.

Format, validation limits, capture/ownership rules, and the compatibility policy are specified in
[`Docs/CoreAIMods/WORLD_PACKAGE.md`](../../../Docs/CoreAIMods/WORLD_PACKAGE.md).

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
- [WORLD_PACKAGE.md](../../../Docs/CoreAIMods/WORLD_PACKAGE.md) — the `.world` package format, validation limits, autosave durability, and session replacement
- [PROCEDURAL_MATERIALS.md](../../CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md) · [TEXTURE_MATERIALS.md](../../CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md) — the `Enum.Material` render catalogs
