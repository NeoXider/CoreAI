# Demo: World Commands

**What you will see:** the plumbing every AI action rides on, with the AI removed — press a button, an
envelope goes through the real router, and an object spawns, moves, changes colour or disappears.

Scene: `WorldCommandsDemo.unity`. No LLM or Lua required.

## What It Shows

The raw CoreAI AI-command pipeline: the same path used to apply actions from LLM agents
(tool `world_command`), Lua bindings, and server commands:

```
WorldCommandsDemoController
  -> IAiGameCommandSink.Publish(ApplyAiGameCommand { CommandTypeId = WorldCommand, JsonPayload })
  -> MessagePipe -> AiGameCommandRouter (main thread)
  -> CoreAiWorldCommandExecutor (spawn / move / set_color / destroy ...)
```

The OnGUI buttons publish `CoreAiWorldCommandEnvelope` envelopes (spawn an enemy from
`CoreAiPrefabRegistryAsset`, move and recolor `Boss`, destroy).

The scene demonstrates the modular composition layout: `CoreAILifetimeScope` has a child
`Lua and World Commands` object whose `CoreAiLuaWorldModule` owns the prefab whitelist and scene-access
configuration. The root scope contains no primary Lua/world settings.

## Why It Exists

- Quickly verify that the scene router/executor/prefab registry are configured correctly before
  connecting an LLM.
- Reference for custom systems: how to publish commands into the shared pipeline from any game code.

Supported executor actions: see `CoreAiWorldCommandExecutor.TryExecute`
(`spawn`, `move`, `destroy`, `set_active`, `parent`, `set_scale`, `set_color`, `load_scene`,
`play_animation`, `play_sound`, `apply_force`, `set_velocity`, etc.).
