# Demo: Lua Mods

Scene: `LuaModsDemo.unity`. No LLM is required; the demo shows the runtime used by AI.

## What Is Inside

- **`LuaModsDemoController`** resolves `ILuaModRuntime` and `LuaCsLogicSlots` from DI and draws an OnGUI panel.
- **`WaveDirectorMod.lua.txt`** is a mod with the `Read | WorldEdit` level:
  - `hooks_on("wave_started", ...)` spawns a wave of enemies in one transaction
    (`coreai_world_begin/commit`), and stores the wave counter in persistent store (`store_set/get`);
  - `hooks_every(4.0, ...)` recolors `Boss` through `coreai_world_set_color`;
  - `events_emit("wave_spawned", n)` sends an event back to the game (`ModEventEmitted`).
- **`DamageTunerMod.lua.txt`** is a mod with the `Read | LogicOverride` level: on load it calls
  `logic_define("damage_formula", ...)`. The controller calls `slots.TryInvokeNumber(...)` every
  frame and shows which formula is active: Lua override or C# default.

## How to Use It

1. Open the scene and press Play.
2. `Load mod` -> `Emit 'wave_started'`: a wave of capsules appears on the floor, and the mod event is visible in the corner.
3. `Load override mod`: the damage formula changes from `atk - def` (C#) to `atk * 2 - def * 0.5` (Lua).
4. `Unload + reset slot`: returns to the C# default.

## What to Verify Visually

- The `wave_director` mod has no `logic_define` (no `LogicOverride` level), and `damage_tuner`
  has no `coreai_world_spawn`: capability levels really restrict the global set.
- The wave counter survives mod Unload/Load (stored in `FileLuaModStore`,
  `persistentDataPath/CoreAI/LuaMods`).
- API details: `Assets/CoreAI/Docs/LUA_GAME_API.md`; security:
  `Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md`.
