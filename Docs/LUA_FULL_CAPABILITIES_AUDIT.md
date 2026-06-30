# Lua Full — capability audit (components, creation, timers)

Date: 2026-06-30. Manual audit answering: "can Lua Full work with components, create things, set up timers?"

## TL;DR

Yes — across two cooperating layers:

| Capability | Where | API | Status |
|------------|-------|-----|--------|
| Read/write any component member | Full tier (`CoreAiFullUnityLuaRuntimeBindings`) | `unity_get_member` / `unity_set_member` / `unity_list_members` | ✅ works, now with rich coercion (Color/Vector/Quaternion/Rect/Bounds/Color32/enum/object-ref) |
| Call any component method | Full tier | `unity_call(id, type, method, args)` | ✅ works (single overload; ambiguous overloads rejected) |
| List components on an object | Full tier | `unity_list_components(id)` | ✅ |
| Find / describe / hierarchy | Full tier | `unity_find`, `unity_find_all`, `unity_find_by_tag`, `unity_find_by_component`, `unity_describe_object`, `unity_get_children` | ✅ |
| Transform edits | Full tier | `unity_set_position/rotation_euler/scale`, `unity_parent` | ✅ |
| **Add a component (reflection)** | Full tier | `unity_add_component(id, type)` | ✅ resolves any Component type (Rigidbody, Light, …), honors blacklist |
| **Destroy an object (reflection)** | Full tier | `unity_destroy(id)` | ✅ DestroyImmediate in edit mode, Destroy at runtime |
| **Add / remove a component (curated)** | Component tier (`CoreAiComponentLuaRuntimeBindings`) | `coreai_component_add(name,type)` / `coreai_component_remove(name,type)` | ✅ curated catalog (no reflection), WebGL-safe |
| Set curated component props | Component tier | `coreai_component_set_number/bool/text/vector` | ✅ |
| **Spawn / create objects** | World tier (`CoreAiWorldLuaRuntimeBindings`) | `coreai_world_spawn(key,name,x,y,z,[rx,ry,rz,scale])` | ✅ primitives + prefabs, now with readable auto-names |
| Destroy / activate | World tier | `coreai_world_destroy`, `coreai_world_set_active` | ✅ |
| **Timers / scheduling** | Mod runtime (`LuaModRuntime`) | `hooks_every(seconds, fn)` (repeating), `hooks_on(event, fn)` (event) | ✅ ticked by `LuaModRuntimeTicker` |
| Events between mods | Mod runtime | `events_emit`, per-mod `store_set/get` | ✅ |

## How the layers fit

1. **Full tier** (`unity_*`) — arbitrary reflection over any component/member/method. Admin/debug power,
   capability-gated (`LuaCapabilities.Full`), off by default, disabled on WebGL. This is where the
   4.16.0 coercion work landed (object references, Rect/Bounds/Color32, numeric widths, enum-by-number).

2. **Component tier** (`coreai_component_*`) — curated, reflection-free add/remove/set for a fixed catalog
   (rigidbody, colliders, light, audiosource, camera, renderers, particlesystem, …). Safe default that
   works on every platform including WebGL.

3. **World tier** (`coreai_world_*`) — spawn/move/destroy/scene. Now auto-names unnamed spawns readably
   (`cube_1`) instead of GUID hashes.

4. **Mod runtime** (`LuaModRuntime` + `LuaModRuntimeTicker`) — persistent mods with `hooks_on` (events)
   and `hooks_every` (repeating timers), per-mod state, inter-mod events. This is the "set up a timer and
   keep doing things" surface.

## So: "can Lua Full create components, call them, set up timers?"

- **Create/add components** — yes, two ways: `unity_add_component(id, type)` (Full-tier reflection, any
  Component type) or `coreai_component_add` (curated, WebGL-safe). `unity_call` can also invoke methods.
- **Work with components** — yes, fully: read/write members with rich type coercion (Color hex/table,
  Vector2/3/4, Quaternion, Rect, Bounds, Color32, enums by name OR number, numeric widths, and Unity
  object references by instance id), call methods, list, discover members with `unity_list_members`.
- **Destroy objects** — yes, `unity_destroy(id)` (Full tier) or `coreai_world_destroy` (world tier).
- **Timers** — yes, via the mod runtime's `hooks_every` (verified by `LuaModRuntimeEditModeTests`:
  `hooks_every(0.1, ...)` ticks repeatedly).

This is a complete game-authoring surface: a Lua mod can create objects, add/configure/wire components
(including assigning object references), call methods, run repeating timers, react to events, persist
state, and talk to other mods. The demos `ModdableUnits`, `WaveAutoBattlerMods`, and `LiveMechanicsMods`
are games built entirely this way.

## Verified by tests

`CoreAiFullUnityLuaRuntimeEditModeTests` (16 cases, all green) covers: member discovery, every coercion
type above, object-reference-by-id assignment, did-you-mean errors, `unity_add_component`, and
`unity_destroy`.

## Gaps / notes

- `unity_call` supports only **non-ambiguous** methods; overloaded methods are rejected by design.
- WebGL: Full tier is intentionally disabled (`CoreAILifetimeScope` forces it off, `link.xml` aligned);
  the curated component + world + timers tiers work on WebGL.
