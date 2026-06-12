# Live Mechanics Mods Chat Demo

Scene: `Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity`

This is a copy of `LiveMechanicsDemo` focused on chat-driven Lua mods.

## What is different from LiveMechanics

- The base battle loop is the same: hero attacks a boss, and Lua logic slots can override
  `damage_formula`, `attack_interval`, `loot_formula`, and `boss_reward`.
- The chat workflow is different: ask the Programmer role to use `manage_mods` with `load`,
  `reload`, `get_source`, `list`, and `unload`.
- The scene adds `LiveMechanicsModsChatPersistenceController`, a host policy that saves successful
  `LuaModRuntime` load/reload sources through `ILuaScriptVersionStore`.
- When the scene starts again, saved mod sources are autoloaded into `LuaModRuntime`.
- `unload` removes the mod from this scene's autoload set.

`LuaModRuntime` itself still stays generic and does not autoload arbitrary source. Autoload is a
scene/host decision, which keeps production games in control of which mods are trusted and restored.

## Example chat prompt

Ask in the chat:

```text
Load a Lua mod named boss_reward_1000. It should change boss rewards to 1000 coins.
Use manage_mods and valid MoonSharp Lua syntax.
```

Expected loaded mod source:

```lua
logic_define('boss_reward', function(bossMaxHp)
    return 1000
end)

report('Boss reward mod loaded: 1000 coins.')
```

After the tool succeeds, restart Play Mode or reload the scene. The right-side demo panel should say
that one saved mod was autoloaded, and the left LiveMechanics panel should list the loaded mod.

## Persistence

Saved mod sources are stored under keys prefixed with:

```text
demo.live_mechanics.mods_chat.mod.
```

The data is written through `ILuaScriptVersionStore`, normally backed by
`persistentDataPath/CoreAI/LuaScriptVersions` in the Unity host package.
