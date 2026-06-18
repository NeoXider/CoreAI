# CoreAI Lua Access Modes

Date: 2026-06-18. Related implementation: `LuaCapabilities`,
`AggregatingGameLuaRuntimeBindings`, `CoreAiFullUnityLuaRuntimeBindings`,
`CoreAILifetimeScope.enableFullLuaAccess`.

## Concept

CoreAI uses explicit trust levels for Lua written by a model. Each level is a flag in
`LuaCapabilities`; bindings are registered only for the intersection of script level and host level.
If a capability is absent, the corresponding functions are physically absent from Lua globals.

| Mode | Flag | What Lua can do |
|---|---|---|
| Read-only | `Read` | Logs, world queries, versions, with no side effects |
| Gameplay | `Gameplay` | Time scale, UI text, sound, animations |
| WorldEdit | `WorldEdit` | Spawn/move/destroy, scenes, batch world commands |
| Logic | `LogicOverride` | `logic_define`, mods (`hooks_on`, `manage_mods`) |
| Full | `Full` | Reflection-style access to `GameObject`/components through `unity_*` APIs |

`LuaCapabilities.All` includes standard tiers except `Full`. Full mode is enabled explicitly through:

- the **Enable Full Lua Access** checkbox on `CoreAILifetimeScope`;
- `LoadMod(..., caps | Full)` or `manage_mods` with host-granted capabilities.

## Full Mode

Full mode exposes public component fields and methods by default. Non-public members require the
host's private-access opt-in. Hosts can register `IFullLuaAccessBlacklistPolicy` to deny component
types or specific members.

```csharp
public interface IFullLuaAccessBlacklistPolicy
{
    bool IsTypeAllowed(Type componentType);
    bool IsMemberAllowed(MemberInfo member);
}
```

The host policy is checked before object discovery, get/set and call operations.

Recommended deny-list entries include `System.*`, `UnityEngine.Application.Quit`, and any network or
file APIs that a host game exposes through components.

## Full API Surface

- `unity_find(name)` -> instance id
- `unity_list_objects(max)`
- `unity_find_all(pattern, max)`
- `unity_find_by_tag(tag, max)`
- `unity_find_by_component(type, max)`
- `unity_describe_object(id)`
- `unity_get_transform(id)`
- `unity_get_children(id)`
- `unity_get_position(id)` / `unity_set_position(id, x, y, z)`
- `unity_set_rotation_euler(id, x, y, z)`
- `unity_set_scale(id, x, y, z)`
- `unity_parent(childId, parentId)`
- `unity_list_components(id)`
- `unity_get_member(id, componentType, memberName)`
- `unity_set_member(id, componentType, memberName, value)`
- `unity_call(id, componentType, methodName, ...)`

## Runtime Guardrails

- Full mode is opt-in.
- Lua chunks and mod handlers still run with MoonSharp sandbox and instruction/time limits.
- `CoreAiFullUnityLuaRuntimeBindings` caches `Type` and `MemberInfo` lookups, but does not bypass
  sandbox limits.
- Mod error budget and auto-unload still apply to persistent mods.
- `luaAllowedScenes` on `CoreAILifetimeScope` constrains scene-loading commands.

## Mod LLM Tools

`manage_mods` (`list`, `get_source`, `load`, `reload`, `unload`) and `execute_lua` are registered in
`WorldCommandsInstaller`. Mod source is retrieved through `LuaModRuntime.TryGetModSource`.

Programmer guidance keeps these tools direct: run a one-shot `execute_lua` diagnostic first, inspect
`Success` / `Output` / `Error`, then use `manage_mods` for persistent hook/timer behavior.

## Extending World Commands

`ICoreAiCustomWorldCommandHandler` and `CoreAiWorldCommandExecutor.RegisterCustomHandler` let a game
add its own actions without modifying the package.

## Demos

- `LiveMechanics` - logic slots + chat + LLM.
- `FullAccess` - Full mode + chat + LLM with explicit host opt-in.
