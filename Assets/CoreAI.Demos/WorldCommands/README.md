# Demo: World Commands

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

## Why It Exists

- Quickly verify that the scene router/executor/prefab registry are configured correctly before
  connecting an LLM.
- Reference for custom systems: how to publish commands into the shared pipeline from any game code.

Supported executor actions: see `CoreAiWorldCommandExecutor.TryExecute`
(`spawn`, `move`, `destroy`, `set_active`, `parent`, `set_scale`, `set_color`, `load_scene`,
`play_animation`, `play_sound`, `apply_force`, `set_velocity`, etc.).
