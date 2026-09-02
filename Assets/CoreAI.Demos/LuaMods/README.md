# Demo: Lua Mods

**What you will see:** the mod runtime the AI drives, with no AI in the room — load a Lua mod, watch it
spawn a wave and override the damage formula, unload it, and see the C# default come back.

Scene: `LuaModsDemo.unity`. No LLM is required; the demo shows the runtime used by AI.

## What Is Inside

- **`LuaModsDemoController`** resolves `ILuaModRuntime` and `LuaCsLogicSlots` from DI and draws an OnGUI panel.
- **`WaveDirectorMod.lua.txt`** is a mod with the `Read | WorldEdit` level:
  - `hooks_on("wave_started", ...)` spawns a wave of enemies as Rbx parts
    (`Instance.new("Part")`, an upright cylinder matching the old `enemy.basic` capsule),
    and stores the wave counter in persistent store (`store_set/get`);
  - `hooks_every(4.0, ...)` recolors `Boss` through an Rbx overlay part (the scene Boss is
    not an Rbx instance, so the mod covers it with a same-spot part it owns and colors that);
  - `events_emit("wave_spawned", n)` sends an event back to the game (`ModEventEmitted`).

  > The mod builds exclusively through the Rbx API: `CoreAiModsInstaller` sets
  > `RegisterWorldEditBuildBindings = false`, so the `coreai_world_*` build APIs are withheld
  > stubs in production. `coreai_world_exists` (Read tier) is unaffected and still guards the
  > Boss recolor. See [RBX_API.md](../../CoreAI/Docs/RBX_API.md).
- **`DamageTunerMod.lua.txt`** is a mod with the `Read | LogicOverride` level: on load it calls
  `logic_define("damage_formula", ...)`. The controller calls `slots.TryInvokeNumber(...)` every
  frame and shows which formula is active: Lua override or C# default.
- **`Lua and World Commands` child module** owns the scene's prefab whitelist and Lua access tier;
  `CoreAILifetimeScope` only composes the optional module into the runtime container.

## How to Use It

1. Open the scene and press Play.
2. `Load mod` -> `Emit 'wave_started'`: a wave of capsules appears on the floor, and the mod event is visible in the corner.
3. `Load override mod`: the damage formula changes from `atk - def` (C#) to `atk * 2 - def * 0.5` (Lua).
4. `Unload + reset slot`: returns to the C# default.

## What to Verify Visually

- The `wave_director` mod has no `logic_define` (no `LogicOverride` level), and `damage_tuner`
  has no world-building globals at all: capability levels really restrict the global set.
- The wave counter survives mod Unload/Load (stored in `FileLuaModStore`,
  `persistentDataPath/CoreAI/LuaMods`).
- API details: `Assets/CoreAI/Docs/LUA_GAME_API.md`; security:
  `Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md`.
