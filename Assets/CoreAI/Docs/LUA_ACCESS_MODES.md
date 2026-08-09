# CoreAI Lua Access Modes

Date: 2026-07-15. Related implementation: `LuaCapabilities`,
`LuaCsGameplayBindings`, `LuaCsFullUnityRuntimeBindings`,
`CoreAiLuaWorldModule`.

## Concept

CoreAI uses explicit trust levels for Lua written by a model. Each level is a flag in
`LuaCapabilities`; bindings are registered only for the intersection of script level and host level.
If a capability is absent, the corresponding functions are physically absent from Lua globals.

| Mode | Flag | What Lua can do |
|---|---|---|
| Read-only | `Read` | Logs (`print`/`report`), world queries, version stores, with no side effects |
| Gameplay | `Gameplay` | `time_*` (time scale, etc.), `input_*` (keyboard/mouse, read-only) |
| WorldEdit | `WorldEdit` | The [Rbx API](RBX_API.md) — `Instance.new`, `workspace`, `instance:Destroy()` — is how a mod spawns/moves/destroys world objects. The classic `coreai_world_*` build commands (spawn/change/destroy, scenes, batch/grid, transactions, sound/animation) are **not registered in the default production composition** (`RegisterWorldEditBuildBindings = false`): each one is a stub that raises an error pointing at the Rbx API. They only work on a host that deliberately opts in |
| Logic | `LogicOverride` | `logic_define`/`logic_reset`/`logic_list` |
| Full | `Full` | Reflection-style access to `GameObject`/components through `unity_*` APIs |

The mod-runtime surface itself — `hooks_on`/`hooks_every`, `store_set`/`store_get`, `print`/`report`,
`events_emit`, `mods_export`/`mods_get`/`mods_call`/`mods_list_exports`, `mod_id` — is **available to
every loaded mod regardless of capability tier**, including a mod loaded with `LuaCapabilities.None`.
These are not game bindings gated by `LuaCsGameplayBindings`; `LuaCsModRuntime` registers
them unconditionally when it builds a mod's script. The capability tier only controls which *game*
APIs (`coreai_world_*`, `time_*`, `input_*`, `unity_*`, ...) a mod's hooks and timers can then call.

`LuaCapabilities.All` includes standard tiers except `Full`. Full mode is enabled explicitly through:

- the **Enable Full Access** checkbox on the optional `CoreAiLuaWorldModule` child of
  `CoreAILifetimeScope`;
- `LoadMod(..., caps | Full)` or `manage_mods` with host-granted capabilities.

**Persisted and shared mods are non-Full by default.** A mod that is rehydrated from the source store
on startup, imported from a bundle, or copied between players never auto-acquires `Full`:
`LuaCsModRuntime.RehydrateFromStore` and `ImportMod` intersect the mod's requested capabilities with the
host grant and then strip `Full` unless the host explicitly passes `allowFull: true`. A shared
capability set is only ever a request; the receiving host decides. See
[LUA_GAME_API.md § Persistence & Sharing](LUA_GAME_API.md) and [FIRST_MOD.md](FIRST_MOD.md).

### Hub Mods tab

The live **Mods** tab (`CoreAiModsHubBinder`) carries the same rule. Its `allowFullTier` flag
**defaults to `false`** (safe): with it off, every mod loaded, imported, shared, or rehydrated through
the tab has `Full` stripped, so an untrusted mod can never self-escalate to reflection from its own
`@coreai` header. Granting `Full` is a deliberate host decision, never derived from the mod. To run
trusted, first-party, or singleplayer content at Full tier, tick **Allow Full Tier** on the binder in
the Inspector — this is the only way `Full` reaches a mod loaded through the Mods tab.

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
- Lua chunks and mod handlers still run with the Lua-CSharp sandbox and instruction/time limits.
- `LuaCsFullUnityRuntimeBindings` caches `Type` and `MemberInfo` lookups, but does not bypass
  sandbox limits.
- Mod error budget still applies to persistent mods: repeated failures quarantine the mod (kept loaded, dispatch suspended; reload clears it).
- `Allowed Scenes` on `CoreAiLuaWorldModule` constrains scene-loading commands.
- **Runtime lifecycle is scope-bound.** The `DontDestroyOnLoad` `CoreAI_LuaModTicker` that drives
  `hooks_on`/`hooks_every` handlers is destroyed via the owning container's dispose callback, so mod
  ticking stops when the scope is disposed instead of leaking a live runtime into later scenes. The
  `FileLuaModStore` is disposal-safe: a late `store_set`/`store_get` (or a `Set`/`Get`/`Clear`) from a
  handler that ticks once more during teardown degrades to a no-op after `Dispose` instead of throwing.

## Mod LLM Tools

`manage_mods` (`list`, `get_source`, `load`, `reload`, `unload`, `export`, `import`, `forget`,
`versions`, `revert`, `diagnostics`) and `execute_lua` are attached to the built-in Programmer role by
`CoreAiModsInstaller.RegisterCoreAiMods`. Mod source is retrieved through
`LuaCsModRuntime.TryGetModSource`; `export`/`import`/`forget` move mods between players through the
source store.

Programmer guidance keeps these tools direct: run a one-shot `execute_lua` diagnostic first, inspect
`Success` / `Output` / `Error`, then use `manage_mods` for persistent hook/timer behavior.

## Extending World Commands

`ICoreAiCustomWorldCommandHandler` and `CoreAiWorldCommandExecutor.RegisterCustomHandler` let a game
add its own actions without modifying the package.

## Demos

- `LiveMechanics` - logic slots + chat + LLM.
- `FullAccess` - Full mode + chat + LLM with explicit host opt-in.

## WebGL / IL2CPP Stripping Requirements

Verified on Unity 6000.3 WebGL with **Managed Stripping Level = Medium** and
**IL2CPP Code Generation = Faster (smaller) builds** (`OptimizeSize`). Both are recommended:
`OptimizeSize` shrinks Lua-CSharp's generic-method tables enough that the Emscripten linker does
not run out of memory, and Medium stripping keeps the wasm small.

**Consumer projects need no link.xml of their own**: the `com.neoxider.coreaiunity` package ships
a `link.xml` at its root with all the preserve rules below, and Unity's linker picks up package
link.xml files automatically. You only have to set the two player settings above (they cannot ship
in a package): Managed Stripping Level = **Medium** (not High) and IL2CPP Code Generation =
**Faster (smaller) builds**. Write your own `link.xml` only if you add your own Lua binding or
DI-registered assemblies (template — replace the assembly name):

```xml
<linker>
  <!-- your assembly with Lua runtime-bindings classes or VContainer-registered types -->
  <assembly fullname="YourGame.Mods" preserve="all"/>
</linker>
```

Reflection-based Lua features keep working **only** with the preserve rules below (already present
in the package `link.xml`):

- `Lua.dll` / `Lua.Annotations.dll` (bundled under `Assets/CoreAIMods/Plugins/`) —
  `preserve="all"`. The Lua-CSharp runtime invokes host delegates and its own loaders via
  reflection on AOT.
- `UnityEngine.CoreModule`: `UnityEngine.Resources` and `UnityEngine.TextAsset` —
  the Lua script loader reaches them purely via reflection; stripping them
  crashes the player with `RuntimeError: null function`.
- Every Lua-CSharp runtime-bindings class the player uses
  (`LuaCsWorldRuntimeBindings`, `LuaCsWorldQueryBindings`, `LuaCsTimeBindings`,
  `LuaCsInputRuntimeBindings`, `LuaCsFullUnityRuntimeBindings`, ...) — their callback
  bodies are reflection-invoked by the Lua-CSharp runtime.

DI note: VContainer's `Register<T>()` finds constructors via reflection; under Medium stripping
an unused parameterless ctor can be stripped and container build fails with
"Type does not found injectable constructor". Register such types with a factory
(`builder.Register(c => new T(), ...)`) or preserve the type in `link.xml`.
