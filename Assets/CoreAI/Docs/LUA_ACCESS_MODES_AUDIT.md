# Audit of AI Access Modes to the Game (CoreAI Lua)

Date: 2026-06-12. Related implementation: `LuaCapabilities`, `AggregatingGameLuaRuntimeBindings`,
`CoreAiFullUnityLuaRuntimeBindings`, `CoreAILifetimeScope.enableFullLuaAccess`.

## Concept

As in Cursor: several **trust levels** for code written by the model. Each level is a flag in
`LuaCapabilities`; bindings are registered only for the intersection of "script level ∩ host level".
Without the flag, functions are **physically absent** from globals (fail-closed).

| Mode | Flag | What Lua can do |
|-------|------|----------------|
| Read-only | `Read` | Logs, world queries, versions, with no side effects |
| Gameplay | `Gameplay` | Time scale, UI text, sound, animations |
| WorldEdit | `WorldEdit` | spawn/move/destroy, scenes, batch world commands |
| Logic | `LogicOverride` | `logic_define`, mods (`hooks_on`, `manage_mods`) |
| **Full** | `Full` | Reflection access to any `GameObject`/components (`unity_*`) |

`LuaCapabilities.All` = all standard tiers **except Full**. Full is enabled explicitly:
- the **Enable Full Lua Access** checkbox on `CoreAILifetimeScope`;
- or `LoadMod(..., caps | Full)` / `manage_mods` with host-granted caps.

## Full Mode (Implemented)

**Policy: allow-all, blacklist later.** Full currently gives access to public component fields and
methods by default; non-public members require the host's private-access opt-in. The API surface is:

- `unity_find(name)` -> instanceId
- `unity_list_objects(max)`, `unity_find_all(pattern,max)`, `unity_find_by_tag(tag,max)`,
  `unity_find_by_component(type,max)`
- `unity_describe_object(id)`, `unity_get_transform(id)`, `unity_get_children(id)`
- `unity_get/set_position`, `unity_set_rotation_euler`, `unity_set_scale`, `unity_parent`
- `unity_list_components`
- `unity_get_member` / `unity_set_member` / `unity_call`

Caching: `ConcurrentDictionary` for `Type` and `MemberInfo`. The MoonSharp sandbox and the
instruction/time limits for chunks and mod handlers **are not weakened**.

### Planned - Blacklist (Not Implemented)

Future interface idea:

```csharp
public interface IFullLuaAccessBlacklistPolicy
{
    bool IsTypeAllowed(Type componentType);
    bool IsMemberAllowed(MemberInfo member);
}
```

The host registers a policy in DI; `CoreAiFullUnityLuaRuntimeBindings` checks it before
get/set/call. Suggested default deny-list: `System.*`, `UnityEngine.Application.Quit`, and
network/file APIs if they ever enter the reflection surface.

Additional mitigations (roadmap):

- player confirmation before the first Full call in a session;
- mod signatures in `FileLuaModStore`;
- capability from role config (TODO in AgentPromptsManifest).

## Risks

| Risk | Current mitigation |
|------|--------------------|
| The model breaks the scene | Opt-in Full; mod error budget + auto-unload |
| Reflection escape | MoonSharp sandbox; no arbitrary C# |
| Material leak on set_color | Fixed: `MaterialPropertyBlock` in the executor |
| LLM Lua spam | `LuaGenerationRateLimiter` |
| Loading unrelated scenes | `luaAllowedScenes` whitelist on the scope |

## Mod LLM Tools

`manage_mods` (list / get_source / load / reload / unload) + `execute_lua` for Programmer are
registered in `WorldCommandsInstaller`. Mod source: `LuaModRuntime.TryGetModSource`.

Programmer guidance keeps these tools direct (no runtime `SkillSet` by default): run a one-shot
`execute_lua` diagnostic first, inspect `Success` / `Output` / `Error`, and then use `manage_mods`
for persistent hook/timer behavior.

## Extending World Commands

`ICoreAiCustomWorldCommandHandler` + `CoreAiWorldCommandExecutor.RegisterCustomHandler` lets a game
add its own actions without modifying the package.

## Demos

- `LiveMechanics` - logic slots + chat + LLM
- `FullAccess` - Full + chat + LLM (Full opt-in on the scope)

