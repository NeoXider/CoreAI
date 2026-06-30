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
| **Add / remove a component** | Component tier (`CoreAiComponentLuaRuntimeBindings`) | `coreai_component_add(name,type)` / `coreai_component_remove(name,type)` | ✅ curated catalog (no reflection) |
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

- **Create/add components** — yes, via `coreai_component_add` (curated) — and `unity_call` can invoke
  `GameObject.AddComponent`-style methods through reflection when Full is granted.
- **Work with components** — yes, fully: read/write members with type coercion, call methods, list.
- **Timers** — yes, via the mod runtime's `hooks_every` (verified by `LuaModRuntimeEditModeTests`:
  `hooks_every(0.1, ...)` ticks repeatedly).

## Gaps / notes

- `unity_call` supports only **non-ambiguous** methods; overloaded methods are rejected by design.
- There is **no** Full-tier `unity_add_component` / `unity_destroy` yet — add/remove goes through the
  curated component tier, or `unity_call` of a reflected method. A thin `unity_add_component(id, type)`
  could be added for symmetry (follow-up).
- WebGL: Full tier is intentionally disabled; component + world + timers tiers work on WebGL.
