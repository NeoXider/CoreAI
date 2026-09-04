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

`Material` takes an `Enum.Material` item and **every one of the 45 enum items renders**.
`RbxTextureMaterialProvider` is catalog-driven: the packaged `RbxMaterialTextureCatalog` ships 36 CC0
texture-backed surfaces at 1K, so every material the catalog describes renders from the package alone
with nothing imported. A project-local override catalog (`Assets/CoreAIRbxTexturesLocal/Resources/CoreAIRbxTextureCatalogOverride`,
written by the Editor menus `CoreAI/Materials/Download CC0 texture sets (ambientCG)...` and
`CoreAI/Materials/Import Bridge-Megascans folder...`) can give **any** of the 45 items a 2K–4K PBR set
with normal, roughness, optional AO and metalness maps; the override wins per material. Items without
a catalog entry are procedural surfaces from `RbxProceduralMaterialProvider` (opaque, metallic,
organic, transparent, neon, and force-field shader paths). A catalog entry with a missing texture falls
back to the procedural surface with one logged error, never to Unity's pink error shader, and a
material id that is not in the catalog resolves to an opaque **magenta diagnostic material** instead of
failing quietly, so a wrong value is visible on the first frame. Quixel Bridge / Fab (Megascans) sets
may be imported into your own project this way, but their licence forbids redistributing them inside a
package — only the CC0 sets ship with CoreAI.

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

### `MaterialVariant` — your own materials, swapped at runtime

The 45 `Enum.Material` items are CoreAI's defaults, not your limit. A game built on this framework
brings its own surfaces through Roblox's own answer to that problem: a `MaterialVariant` instance
parented to `MaterialService`, selected per part by the string property `BasePart.MaterialVariant`.
No CoreAI-specific API, no additions to `Enum.Material` — the same script runs in Roblox.

```lua
local variant = Instance.new("MaterialVariant")
variant.Name = "MossyBrick"
variant.BaseMaterial = Enum.Material.Brick      -- inherits everything you do not override
variant.ColorMap = "MyGame/Textures/mossy_brick_color"
variant.NormalMap = "MyGame/Textures/mossy_brick_normal"
variant.RoughnessMap = "MyGame/Textures/mossy_brick_rough"
variant.StudsPerTile = 8
variant.Parent = game:GetService("MaterialService")

wall.MaterialVariant = "MossyBrick"   -- swap it on
wall.MaterialVariant = ""             -- and back to plain Enum.Material.Brick
```

The map strings are `Resources` paths inside your own project, so shipping a texture pack is
dropping files under any `Resources/` folder and naming them from Lua. A map you leave empty keeps
the base material's own texture, so a variant that only recolours a surface is three lines.
`StudsPerTile` is the variant's own tile width in studs — it always applies, exactly as in Roblox, so
a variant inherits its base material's *textures* but not its tiling.

Assignments take effect on the frame they are made. So does editing a variant that parts are already
wearing: changing its maps, its `BaseMaterial` or its `StudsPerTile` repaints every part using it, and
so does renaming, destroying or reparenting the variant itself — the shared material is mutated in
place rather than reallocated, so no part has to be touched. Naming a variant that does not exist
renders the plain `Material` instead; it is not an error, and nothing goes magenta. Variants and the
parts referencing them both survive a world save and load.

Supported today: `BaseMaterial`, `ColorMap`, `NormalMap`, `RoughnessMap`, `MetalnessMap`,
`StudsPerTile`. Roblox's `AlphaMode`, `MaterialPattern`, `CustomPhysicalProperties`, the emissive
properties and the `*Content` accessors are not implemented yet.

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

## Mutation envelopes, access control and disconnects

Every production path that can mutate the world runs under a **server-generated mutation envelope**:
the `execute_lua` tool (plain and MCP), a mod's main chunk, every scheduler resume
(`task.wait`/`task.spawn`/`task.delay`, `Heartbeat`, `RenderStepped`), deferred signal and
`RemoteEvent`/`RemoteFunction` handler dispatch, and cross-mod calls (which run under the callee's
actor). The AI never supplies an operation id, target instance id or expected revision — the tool
schemas expose only `code`. In an ACL-versioned world a caller that reaches the instance registry with
no envelope at all is refused with `BAD_ARGUMENT`, a duplicate operation id applies once, and the
engine-free `WorldAclAuthorizer` inside `CoreAI.RbxApi.Instances` refuses `SetAccessControl`, parent,
property and `Destroy` mutations by actor identity — the Lua bindings are no longer the only guard.

Inbound network input never creates identity: a `RemoteEvent`/`RemoteFunction` message from a sender
that the bridge has not admitted is refused with a structured error before any lookup, decoding or
`Player` allocation.

`RemoteEvent` and `RemoteFunction` argument envelopes have a fixed **65,536-byte UTF-8** wire limit.
CoreAI rejects larger inbound or outbound envelopes with `PAYLOAD_TOO_LARGE` and never truncates
them; inbound size is checked before the UTF-8/JSON string is materialised. Split large application
payloads across several events.

Disconnecting an actor is one production seam: it unregisters the actor from the bridge, fires
`Players.PlayerRemoving` **exactly once** (with the documented `PlayerExitReason`), releases the actor's
chat service, kills the actor's scheduler threads and drops its rate windows and client signals. A
second disconnect is a no-op, and other actors are untouched; 200 connect/disconnect cycles leave no
retained state.

## Saving and loading a world

A world is one `.world` ZIP container (`manifest.json` + `world.json` + indexed `Mods/NNNN/`
entries) holding the **world-owned** instance tree, world settings including `meters_per_stud`,
optional camera state, and the exact Lua mod sources. Mod-ephemeral subtrees (anything under a node
with an `OwnerModId`) are deliberately excluded: mods restart clean and recreate their own objects
after a load.

**Deviation from Roblox — `Archivable`.** Roblox omits instances with `Archivable = false` when a
place is saved. The CoreAI world package keeps them (the flag round-trips as durable state) so a
runtime restart reproduces the exact world an AI built; nothing is silently dropped on the way to
disk. Filter such instances yourself before `save_world` if you rely on the Roblox behaviour.

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
