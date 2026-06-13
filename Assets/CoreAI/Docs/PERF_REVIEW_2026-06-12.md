# CoreAI Lua / World Performance Review (2026-06-12)

Brief audit of hot paths after the Lua v4 work. Status: **critical items fixed**; everything else is backlog.

## Fixed (Critical)

### `set_color` and Material Instances

**Before:** `renderer.material.color = ...` created a new Material instance on every call
(GPU/CPU leak during frequent AI recoloring).

**After:** `MaterialPropertyBlock` is reused on the executor (`_sharedColorMpb`), with
`SetPropertyBlock` for `_Color` / `_BaseColor`.

File: `CoreAiWorldCommandExecutor.TrySetColor`.

### `LuaModRuntime.Tick`: Array Allocation Every Frame

**Before:** `new Mod[_mods.Count]` + `CopyTo` on every tick.

**After:** reusable `List<Mod> _tickScratch` (Clear + fill under lock, iterate without lock).

## Non-Critical / Backlog (TODO)

| Area | Observation | Recommendation |
|---------|------------|--------------|
| `GameObject.Find` | Called on every world/Lua query by name | Cache name->id with invalidation on destroy, or use instanceId from `unity_find` |
| Full reflection | `Type.GetType` + assembly scan on first access | Already cached in `ConcurrentDictionary`; monitor cold start |
| `LuaCoroutineRunner.Update` | Linear pass over `_handles`; prune through `_toRemove` | Acceptable with cap 64; if it grows, use swap-remove |
| `LuaModsLlmTool.ListMods` | Allocates DTO list per call | OK for an LLM tool (not per-frame) |
| Chat UI | Streaming through MEAI callbacks | No Update() polling; no unnecessary work found |
| `DynValue.FromObject` in mod events | Boxes args in handlers | Bounded by mod runtime limits |

## Methodology

Static code review + selective EditMode/PlayMode runs (Lua EditMode 94 passed,
FastNoLlm PlayMode 24 passed, `LuaDynamicGameMechanicsTests` with LM Studio passed).

## Related Documents

- `LUA_ACCESS_MODES_AUDIT.md` - access modes and Full
- `LUA_SANDBOX_SECURITY.md` - sandbox limits

