# World Commands — controlling the world from Lua (runtime)

**Goal:** Let the **Programmer** role (and eventually **Creator**) **safely** change the world at runtime: spawn/move/enable objects, switch scenes, and more.

**Core idea:** Lua does **not** touch Unity directly. Lua calls a **whitelist API** that publishes a typed command on the bus. The Unity layer executes the action on the **main thread**.

---

## 1. Data flow (canonical)

1. LLM → `ApplyAiGameCommand` with `CommandTypeId = AiEnvelope`
2. `LuaAiEnvelopeProcessor` extracts Lua and runs it in `SecureLuaEnvironment`
3. Lua calls `coreai_world_*` → publishes `ApplyAiGameCommand` with `CommandTypeId = WorldCommand`
4. `AiGameCommandRouter` on the main thread calls `ICoreAiWorldCommandExecutor.TryExecute(...)`

This preserves:
- **Main-thread safety** for Unity
- **Control / logging** via MessagePipe and `traceId` (when present)
- Extensibility via interfaces and registries

Direct MEAI tool calls through `WorldLlmTool` use the same safety rule: before invoking
`ICoreAiWorldCommandExecutor.TryExecute(...)`, the tool switches to the Unity main thread.

---

## 2. Lua API (whitelist)

Built-in functions:

- `coreai_world_spawn(prefabKeyOrName, targetName, x, y, z, [rx, ry, rz, scale]) -> bool` — spawn at position (x,y,z); optional rotation (rx,ry,rz degrees) and uniform scale; separate `coreai_world_rotate`/`coreai_world_set_props` also work post-spawn
- `coreai_world_move(targetName, x, y, z)`
- `coreai_world_rotate(targetName, x, y, z)`
- `coreai_world_set_transform(targetName, x, y, z, rx, ry, rz, scale)`
- `coreai_world_destroy(targetName)`
- `coreai_world_set_active(targetName, active)`
- `coreai_world_parent(childName, parentName)` (`""` / `"none"` detaches)
- `coreai_world_set_props(targetName, { scale=..., color=... })`
- `coreai_world_load_scene(sceneName)`
- `coreai_world_reload_scene()`
- `coreai_world_play_animation(targetName, animationName)`
- `coreai_world_list_animations(targetName)`
- `coreai_world_show_text(targetName, textToDisplay)`
- `coreai_world_apply_force(targetName, fx, fy, fz)`
- `coreai_world_spawn_particles(targetName, prefabKeyOrName)`
- `coreai_world_list_objects(searchPattern)`
- `coreai_component_add(targetName, componentType)`
- `coreai_component_remove(targetName, componentType)`
- `coreai_component_set_number(targetName, componentType, propertyName, value)`
- `coreai_component_set_bool(targetName, componentType, propertyName, value)`
- `coreai_component_set_text(targetName, componentType, propertyName, value)`
- `coreai_component_set_vector(targetName, componentType, propertyName, x, y, z)`

### Key recommendations

- **prefabKeyOrName:** Prefer a **GUID string** (or another stable id), a prefab name from the registry, or a built-in primitive key (`cube`, `sphere`, `cylinder`, `capsule`, `plane`, `quad`, `empty`).
- **targetName:** Scene object name (`GameObject` name). Commands resolve objects dynamically on the Unity side; Lua code does not call `GameObject.Find()`.
- **Transform commands:** `coreai_world_move`, `coreai_world_rotate`, and `coreai_world_set_transform` are part of normal `WorldEdit` access, not Full mode.
- **Animation commands:** `coreai_world_play_animation`, `coreai_world_list_animations`, and direct `world_command` actions `play_animation` / `list_animations` require `targetName`; pass it as a structured argument, not only in prose.
- **Colour commands:** colour changes may still exist in lower-level command envelopes / executors, but `set_color` is not exposed as a model-callable direct `world_command` action.
- **Component commands:** `coreai_component_*` functions publish `ComponentCommand` envelopes. They use the curated component catalog below and do not use reflection.

---

## 3. Spawning prefabs and built-in primitives

Spawning first tries `CoreAiPrefabRegistryAsset` (ScriptableObject registry). If the requested `prefabKey` is not registered and world primitives are allowed, the executor creates a built-in Unity primitive directly.

Accepted primitive keys:

- `cube`
- `sphere`
- `cylinder`
- `capsule`
- `plane`
- `quad`
- `empty` (creates an empty `GameObject`)

This means `world_command` / `coreai_world_spawn` works out of the box with no prefab registry for simple prototypes. Registered prefab keys still take precedence over primitive fallback.

### How to wire it

1. Create asset: **Create → CoreAI → World → Prefab Registry**
2. Fill **Key** (GUID string) and/or **Name**, assign **Prefab**
3. On `CoreAILifetimeScope`, assign **World Prefab Registry**

If the registry is not assigned, primitive spawns still work while `Allow World Primitives` is enabled. Non-primitive keys are rejected unless a registry resolves them.

Primitive fallback is gated by `ICoreAISettings.AllowWorldPrimitives` (default `true`), surfaced on the CoreAI Settings asset as **World Commands -> Allow World Primitives**. When disabled, `spawn` is restricted to registered prefab keys.

---

## 4. Extending behavior (project layer)

### 4.1 Add your own world commands

Options:
- **A (recommended):** Extend `ICoreAiWorldCommandExecutor` with your implementation (or a composition wrapper), add new `action` values in the JSON envelope, and handle them on the main thread.
- **B:** A separate `WorldCommandRouter` on MessagePipe that subscribes to `ApplyAiGameCommand` and handles only `WorldCommand` (if you want full isolation from `AiGameCommandRouter`).

### 4.2 Changing components safely

Direct reflection from Lua is risky. Use the native `component_command` LLM tool or the Lua `coreai_component_*` functions for common Unity components.

`component_command` actions:

| Action | Required parameters | Effect |
|---|---|---|
| `add` | `targetName`, `componentType` | Adds the supported component if missing. |
| `remove` | `targetName`, `componentType` | Removes the supported component. |
| `set` | `targetName`, `componentType`, `propertyName`, value field | Sets a supported property and auto-adds the component if missing. |
| `list_components` | `targetName` | Lists component type names on the object. |

Supported `componentType` values: `rigidbody`, `rigidbody2d`, `boxcollider`, `spherecollider`, `capsulecollider`, `meshcollider`, `light`, `audiosource`, `camera`, `linerenderer`, `trailrenderer`, `textmesh`, `meshrenderer`, `particlesystem`.

For `set`, use the matching value field: `floatValue` for numbers, `boolValue` (`0` / `1`) for booleans, `stringValue` for text, HTML colours, and enum names, and `x` / `y` / `z` for vectors.

Examples:

```json
{"name":"component_command","arguments":{"action":"add","targetName":"Cube","componentType":"rigidbody"}}
{"name":"component_command","arguments":{"action":"set","targetName":"Cube","componentType":"rigidbody","propertyName":"mass","floatValue":5}}
{"name":"component_command","arguments":{"action":"set","targetName":"Lamp","componentType":"light","propertyName":"color","stringValue":"#88aa33"}}
```

Lua equivalents:

```lua
coreai_component_add("Cube", "rigidbody")
coreai_component_set_number("Cube", "rigidbody", "mass", 5)
coreai_component_set_bool("Cube", "rigidbody", "useGravity", true)
coreai_component_set_text("Lamp", "light", "color", "#88aa33")
coreai_component_set_vector("Trigger", "boxcollider", "size", 2, 3, 2)
```

---

## 5. Defaults vs configuration

**By default** in the template:
- World Commands are enabled (Lua API registered).
- `spawn` can create built-in primitives with no prefab registry.
- Registered prefabs are still supported through `CoreAiPrefabRegistryAsset`.

**Configurable** via `CoreAILifetimeScope` Inspector:
- Assign or disable the prefab registry
- Replace or wrap the command executor

**Configurable** via the CoreAI Settings asset:
- **World Commands -> Allow World Primitives** controls primitive fallback for `spawn` (default on).

---

## 6. Tests

- EditMode: `WorldCommandLuaBindingsEditModeTests` — verifies Lua publishes `WorldCommand` with valid JSON.
- PlayMode (recommended for your title): smoke test on a scene where `coreai_world_spawn` creates an object from the registry.
