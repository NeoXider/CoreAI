# World Commands - controlling the world from Lua and LLM tools

**Goal:** let the Programmer role, Creator role, and custom agents safely change the Unity world at runtime: spawn objects, apply partial changes, enable/disable objects, switch scenes, play animations/audio, and inspect object names.

Lua and LLM tools do not touch Unity directly. They publish a typed world command; the Unity layer executes it on the main thread through `ICoreAiWorldCommandExecutor`.

## 1. Data flow

1. LLM or Lua emits a world command.
2. Lua calls publish `ApplyAiGameCommand` with `CommandTypeId = WorldCommand`.
3. Direct MEAI calls through `WorldLlmTool` switch to the Unity main thread.
4. `ICoreAiWorldCommandExecutor.TryExecute(...)` applies the command.

This keeps Unity calls main-thread safe, logged, and routed through the same command executor.

## 2. Public LLM `world_command` actions

Current public actions:

| Action | Purpose |
|---|---|
| `spawn` | Create a registered prefab or built-in primitive. |
| `change` | Apply optional position, rotation, scale, or parent changes to an existing object. |
| `set_color` | Set renderer colour from an HTML colour string. |
| `destroy` | Remove an object by name. |
| `load_scene` / `reload_scene` | Load or reload scenes. |
| `set_active` | Enable or disable an object. |
| `play_animation` / `stop_animation` / `list_animations` | Control or inspect animations. |
| `play_sound` / `set_volume` | Control AudioSource playback and volume. |
| `show_text` / `hide_panel` | Show or hide simple UI text/panels. |
| `apply_force` / `set_velocity` | Apply Rigidbody force or velocity. |
| `list_objects` | List known scene objects, optionally filtered by name. |

Removed from the public API and docs: `move`, `rotate`, `set_scale`, `parent`, `set_transform`, `update_score`, and `spawn_particles`.

### `spawn`

Required: `prefabKey`, `targetName`.

Optional: `x/y/z`, `fx/fy/fz`, `scale`, `scaleX/scaleY/scaleZ`, `stringValue` for parent.

```json
{
  "name": "world_command",
  "arguments": {
    "action": "spawn",
    "prefabKey": "cube",
    "targetName": "GateWall",
    "x": 0,
    "y": 1,
    "z": 0,
    "fy": 90,
    "scaleX": 8,
    "scaleY": 2,
    "scaleZ": 0.5,
    "stringValue": "CastleRoot"
  }
}
```

`scale` is a uniform fallback. Use `scaleX/scaleY/scaleZ` when the requested object dimensions matter in meters.

### `change`

Required: `targetName`.

Optional: any subset of `x/y/z`, `fx/fy/fz`, `scale`, `scaleX/scaleY/scaleZ`, `stringValue` for parent. Only supplied fields are changed.

```json
{
  "name": "world_command",
  "arguments": {
    "action": "change",
    "targetName": "GateWall",
    "x": 2,
    "fy": 180,
    "scaleY": 3,
    "stringValue": "none"
  }
}
```

Use `stringValue: "none"` to detach from the current parent.

### `set_color`

```json
{
  "name": "world_command",
  "arguments": {
    "action": "set_color",
    "targetName": "GateWall",
    "stringValue": "#ff3300"
  }
}
```

## 3. Public Lua WorldEdit API

Use table-shaped spawn calls so optional fields are explicit:

```lua
coreai_world_spawn({
  prefab = "enemy.basic",
  name = "Enemy1",
  x = 0,
  y = 0,
  z = 0,
  ry = 90,
  scaleX = 2,
  scaleY = 1,
  scaleZ = 3,
  parent = "Root"
})
```

Change applies only supplied fields:

```lua
coreai_world_change("Enemy1", { x = 2, ry = 180, scale = 1.5, parent = "none" })
coreai_world_set_color("Enemy1", "#ff3300")
coreai_world_destroy("Enemy1")
```

WorldEdit also exposes scene, animation, audio, UI text, physics, object-listing, batch, grid, and transaction helpers where enabled by capabilities.

## 4. Prefabs and built-in primitives

`spawn` first tries `CoreAiPrefabRegistryAsset`. If the requested `prefabKey` is not registered and world primitives are allowed, the executor creates a built-in Unity primitive.

Accepted primitive keys:

- `cube`
- `sphere`
- `cylinder`
- `capsule`
- `plane`
- `empty`

Primitive fallback is gated by `ICoreAISettings.AllowWorldPrimitives` (default `true`), surfaced on the CoreAI Settings asset as **World Commands -> Allow World Primitives**. Registered prefab keys still take precedence.

## 5. Scene tools are separate

`scene_tool` is a separate inspection/edit surface for scene queries and instance-level operations. It is not a replacement for `world_command`, and its `set_transform` action should not be documented as a public world command action. Use `world_command.change` or `coreai_world_change` for name-based gameplay world edits.

## 6. Component commands

Direct reflection from Lua is risky. Use the native `component_command` LLM tool or Lua `coreai_component_*` functions for common Unity components.

| Action | Required parameters | Effect |
|---|---|---|
| `add` | `targetName`, `componentType` | Adds the supported component if missing. |
| `remove` | `targetName`, `componentType` | Removes the supported component. |
| `set` | `targetName`, `componentType`, `propertyName`, matching value field | Sets a supported property and auto-adds the component if missing. |
| `list_components` | `targetName` | Lists component type names on the object. |

Supported `componentType` values: `rigidbody`, `rigidbody2d`, `boxcollider`, `spherecollider`, `capsulecollider`, `meshcollider`, `light`, `audiosource`, `camera`, `linerenderer`, `trailrenderer`, `textmesh`, `meshrenderer`, and `particlesystem`.

Lua equivalents:

```lua
coreai_component_add("Cube", "rigidbody")
coreai_component_set_number("Cube", "rigidbody", "mass", 5)
coreai_component_set_bool("Cube", "rigidbody", "useGravity", true)
coreai_component_set_text("Lamp", "light", "color", "#88aa33")
coreai_component_set_vector("Trigger", "boxcollider", "size", 2, 3, 2)
```

## 7. World State Persistence (auto-save/load)

All AI-spawned objects (primitives and prefabs) are automatically tracked for save/load.

### How it works

Every `spawn` call attaches a `WorldObjectComponent` with a unique `persistentId`. On **Play Mode exit** (or `Application.quitting`), the `WorldStateManager` snapshots all active `WorldObjectComponent` instances to a JSON file at `persistentDataPath/CoreAI/WorldState/world_state.json`. On **next Play Mode entry**, the file is loaded and all objects are re-created.

### What is saved per object

| Field | Description |
|---|---|
| `id` | Stable `persistentId` (GUID) |
| `prefabKey` | Primitive key (`cube`, `sphere`, etc.) or prefab registry key |
| `name` | Current `GameObject.name` |
| `px, py, pz` | World position |
| `rx, ry, rz` | Euler rotation |
| `sx, sy, sz` | Local scale |
| `parent` | Parent object name (empty if root) |
| `active` | Whether the object is enabled |
| `cr, cg, cb, ca` | **Optional** — only present if `set_color` was called on the object; otherwise `-1` (sentinel). Prefabs without explicit `set_color` keep their original material. |

### Color behaviour

- **Only colours set via `set_color` are persisted** — objects with no explicit colour save `cr/cg/cb/ca = -1` and are loaded with their original material.
- On restore, colour is applied via `MaterialPropertyBlock` (same path as `set_color`), so the prefab's base material is not modified.
- Old save files (format version `1.0`, no colour fields) are loaded without colour changes.

### Hub controls

The **World** tab in the CoreAI Hub shows:
- Current saved-state status ("Has saved state: Yes / No")
- **Reset World** — destroys all tracked objects and deletes the save file
- **Save Now** — trigger a manual save at any time

### Save format

```json
{
  "version": "1.1",
  "timestamp": "2026-07-09T08:01:23Z",
  "scene": "CoreAiHubDemo",
  "objects": [
    {
      "id": "a7d957c5-3e15-4599-9c1d-d396e9b7913a",
      "prefabKey": "cube",
      "name": "RedCube",
      "px": 0.0, "py": 1.0, "pz": 0.0,
      "rx": 0.0, "ry": 0.0, "rz": 0.0,
      "sx": 1.0, "sy": 1.0, "sz": 1.0,
      "parent": "",
      "active": true,
      "cr": 1.0, "cg": 0.0, "cb": 0.0, "ca": 1.0
    }
  ]
}
```

### Scene mismatch guard

If the saved scene name differs from the current scene, load is skipped. This prevents accidentally spawning world objects in the wrong scene.

## 8. Tests

- EditMode: `WorldCommandLuaBindingsEditModeTests` verifies Lua publishes valid `WorldCommand` JSON.
- PlayMode: world-command executor and public world-tool tests should cover spawn/change/colour/destroy/listing plus runtime actions such as animation, audio, physics, and UI.
