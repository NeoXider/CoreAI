# Live Mechanics Demo - Real LLM Changes Mechanics Through Chat

**What you will see:** you ask for a critical-hit rule in plain language, the model writes the Lua, and the
fight in front of you starts landing crits — no recompile, no restart.

Scene: `Assets/CoreAI.Demos/LiveMechanics/LiveMechanicsDemo.unity`

Demonstrates the main CoreAI scenario: a **real LLM model** (not a stub) writes Lua code through
in-game chat and creates/changes gameplay mechanics on the fly while the game is running.

## What Is in the Scene

- Mini-game: the hero attacks the boss every N seconds; damage, attack interval, and loot are computed
  through **Lua logic slots** (`damage_formula`, `attack_interval`, `loot_formula`).
  Until a slot is overridden, the C# default is used (`atk - def`, 2 sec, 10 gold).
- `CoreAiChatPanel` with the **Programmer** role: the model runs Lua through the `execute_lua` tool
  (a fenced Lua block in a plain text reply is not executed), which goes through
  `LuaCsGameToolExecutor -> LuaCsSecureEnvironment` with the full standard binding set
  (`LuaCapabilities.All`, no Full): logic slots, `ILuaModRuntime`, and world building through the
  [Rbx API](../../CoreAI/Docs/RBX_API.md) (`Instance.new('Part')`). The classic `coreai_world_*`
  build calls are withheld stubs on this composition; the read-only queries
  (`coreai_world_find`/`_pos`/`_exists`) still work, as does `Shared/DemoPrefabRegistry` for the C#
  JSON world-command pipeline.
- Status panel (uGUI `CoreAiDemoPanel`): boss HP, gold, slot states (C# default / Lua override),
  loaded mods, combat log. Open chat with **C**.

## Requirements

- LM Studio (or any OpenAI-compatible server) at `http://127.0.0.1:1234/v1` with a loaded model;
  the endpoint is configured in `Assets/Resources/CoreAISettings.asset`.
- The Lua-CSharp runtime (shipped via the CoreAI.Mods package), with `COREAI_LUA` defined.

## How to Use It

1. Open the scene and enter Play Mode.
2. Press **C** to open chat.
3. Ask the model to change a mechanic; sample prompts are below.
4. Watch the status panel: the slot switches to `Lua override`, and the numbers in the combat log change immediately.

## Sample Prompts

Creating / changing rules (logic slots):

- "Create a critical hit mechanic: override the `damage_formula(atk, def)` slot so that damage has a 30% chance to double, otherwise use the normal atk - def."
- "Change the combat rule: damage should be (atk - def) * 1.5, minimum 1."
- "Make the hero faster: override `attack_interval` so attacks happen once every 0.5 seconds."
- "Change the economy: `loot_formula(bossMaxHp)` should give bossMaxHp / 10 + 25 gold."
- "Show which slots exist in the game (call `logic_list()`), and reset `damage_formula` to the default through `logic_reset`."

World (world commands):

- "Spawn three enemies with prefab `enemy` around point (0, 1.5, 0) and recolor the boss purple."

Hint for the model if it does not know the API: you can name the functions directly in the prompt:
`logic_define(name, fn)`, `logic_reset(name)`, `logic_list()`, `report(msg)`.

## Persistence

LiveMechanics persists successful chat-driven `execute_lua` rule changes for its known logic slots
(`damage_formula`, `attack_interval`, `loot_formula`, `boss_reward`) through
`ILuaScriptVersionStore` under `persistentDataPath/CoreAI/LuaScriptVersions`. When the scene starts
again, it reapplies the saved Lua chunk before the battle loop continues.

Mods are separate from these rule chunks: a mod loaded through `manage_mods` is saved by the
composition's source store and rehydrated on the next start (this scene's store is isolated by
`storeId = live-mechanics-demo`), and its `store_set` / `store_get` values are file-backed by
`FileLuaModStore`. No extra autoload code is needed for them.

## Security

Model code runs only in `LuaCsSecureEnvironment` (Lua-CSharp sandbox: no io/files, `os` limited to
`os.time`/`os.clock`, instruction and memory limits). Capabilities are restricted by `LuaCapabilities`; the demo intentionally gives the
Programmer role the full standard set (`All`) because it demonstrates an "AI game designer".

## Mods-chat copy

For the same battle loop with chat-driven mod source autoload, use
`Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity`. That copied scene adds a
host mod manager: successful `manage_mods load` / `reload` sources are saved, `F9` toggles the
active/saved mod panel (`F10` is the Token Budget / usage overlay), `Deactivate` moves a mod to the
saved list, and saved inactive mods can be activated again.
The same folder also contains `WaveAutoBattlerModsDemo.unity`, a fuller hero-vs-waves demo where
mods change real combat slots instead of only the boss-rule sandbox.
