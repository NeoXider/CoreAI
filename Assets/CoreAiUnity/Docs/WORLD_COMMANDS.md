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

- `coreai_world_spawn(prefabKeyOrName, targetName, x, y, z) -> bool`
- `coreai_world_move(targetName, x, y, z)`
- `coreai_world_destroy(targetName)`
- `coreai_world_set_active(targetName, active)`
- `coreai_world_load_scene(sceneName)`
- `coreai_world_reload_scene()`
- `coreai_world_play_animation(targetName, animationName)`
- `coreai_world_list_animations(targetName)`
- `coreai_world_show_text(targetName, textToDisplay)`
- `coreai_world_apply_force(targetName, fx, fy, fz)`
- `coreai_world_spawn_particles(targetName, prefabKeyOrName)`
- `coreai_world_list_objects(searchPattern)`

### Key recommendations

- **prefabKeyOrName:** Prefer a **GUID string** (or another stable id) or a prefab name from the registry.
- **targetName:** Scene object name (`GameObject` name). Commands resolve objects dynamically via `GameObject.Find()`.
- **Animation commands:** `coreai_world_play_animation`, `coreai_world_list_animations`, and direct `world_command` actions `play_animation` / `list_animations` require `targetName`; pass it as a structured argument, not only in prose.

---

## 3. Resource whitelist: spawning prefabs

Spawning goes only through `CoreAiPrefabRegistryAsset` (ScriptableObject registry).

### How to wire it

1. Create asset: **Create → CoreAI → World → Prefab Registry**
2. Fill **Key** (GUID string) and/or **Name**, assign **Prefab**
3. On `CoreAILifetimeScope`, assign **World Prefab Registry**

If the registry is not assigned, spawn requests are **rejected**.

---

## 4. Extending behavior (project layer)

### 4.1 Add your own world commands

Options:
- **A (recommended):** Extend `ICoreAiWorldCommandExecutor` with your implementation (or a composition wrapper), add new `action` values in the JSON envelope, and handle them on the main thread.
- **B:** A separate `WorldCommandRouter` on MessagePipe that subscribes to `ApplyAiGameCommand` and handles only `WorldCommand` (if you want full isolation from `AiGameCommandRouter`).

### 4.2 “Reflection” and changing components

Direct reflection from Lua is risky. To “mutate object data,” do it through:
- An allowlist of types/fields/methods
- A dedicated `IWorldReflectionPolicy` (project layer)
- A set of strictly typed commands (e.g. `set_transform`, `add_force`, `set_anim_trigger`)

---

## 5. Defaults vs configuration

**By default** in the template:
- World Commands are enabled (Lua API registered).
- Spawning requires an assigned `CoreAiPrefabRegistryAsset`.

**Configurable** via `CoreAILifetimeScope` Inspector:
- Assign or disable the prefab registry
- Replace or wrap the command executor

---

## 6. Tests

- EditMode: `WorldCommandLuaBindingsEditModeTests` — verifies Lua publishes `WorldCommand` with valid JSON.
- PlayMode (recommended for your title): smoke test on a scene where `coreai_world_spawn` creates an object from the registry.
