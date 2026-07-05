namespace CoreAI.Ai
{
    /// <summary>
    /// Built-in "Lua Modding" skill: the full model-facing Lua reference, loaded on demand via
    /// <c>read_skill</c> so the Programmer system prompt can stay small. The prompt carries the
    /// survival-minimum API list; this text carries the whole reference with worked examples —
    /// enough for a capable model to build a complete mini-game mod without any other source.
    /// </summary>
    public static class BuiltInLuaModdingSkillText
    {
        /// <summary>Catalog name (what the model passes to <c>read_skill</c>).</summary>
        public const string SkillName = "Lua Modding";

        /// <summary>One-line catalog description shown before the model decides to read the skill.</summary>
        public const string SkillDescription =
            "Full CoreAI Lua reference: every sandbox API with signatures, working mod examples " +
            "(timers, input, persistence, cross-mod calls, a complete mini-game skeleton), and the " +
            "common errors. Read this before writing any non-trivial Lua mod.";

        /// <summary>Full instructions returned by <c>read_skill("Lua Modding")</c>.</summary>
        public const string Instructions = @"# CoreAI Lua Modding Reference

Contents: 1. Execution models  2. World API  3. Scene queries  4. Full tier (unity_*)
5. Timers & game loops  6. Persistence  7. Events & cross-mod  8. Player input
9. Logic slots  10. Output  11. Complete mod example  12. Common errors

## 1. Execution models

- `execute_lua` tool: one-shot script. Runs once, returns Output/Error. Use for diagnostics
  and single actions. Nothing survives the call.
- `manage_mods` tool: persistent mods. `load` compiles and runs the source once (register your
  hooks there); handlers then run until `unload`. Mods auto-persist and reload on app restart.
  Actions: list, get_source, load, reload, unload, export, import, forget, versions, revert,
  diagnostics. A hook that throws at runtime surfaces in `diagnostics`, not in the load call.
- Improving an existing mod: `get_source` first, edit that text, then `reload` with the FULL
  updated source (reload replaces the whole mod; never send a fragment). Every reload stores a
  revision: `versions` lists them, `revert` rolls back. `forget` = delete (unload + remove the
  persisted copy) - the panel's Forget button does the same.
- In the `code` JSON string write newlines as standard \n escapes. Never double-escape (\\n
  reaches Lua as a literal backslash and the whole source collapses into one broken line).
- Sandbox: no io/os/require/load/debug. Instruction and time budgets apply; a handler burning
  >100 ms is reported as an error. Keep per-tick work small.

## 2. World API (WorldEdit tier - no Full mode needed)

- `coreai_world_list_prefabs()` -> table of prefab keys. Call before the first spawn.
- `coreai_world_spawn({prefab='key', name='obj1', x=0,y=0,z=0, rx=0,ry=0,rz=0,
   scale=1 or scaleX/scaleY/scaleZ, parent='optionalParentName'})`
  Primitives work as prefab keys: 'cube','sphere','cylinder','capsule','plane','empty'.
- `coreai_world_change(name, {x=,y=,z=, rx=,ry=,rz=, scale=, parent=})` - move/rotate/rescale.
- `coreai_world_set_color(name, '#RRGGBB')`
- `coreai_world_destroy(name)`; `coreai_world_exists(name)` -> bool
- `coreai_world_spawn_batch(list)` and `coreai_world_grid` for many objects at once.
- Transactions: `coreai_world_begin_transaction()` / `commit` / `rollback` to group edits.
- Names are the contract: pick unique names at spawn and drive everything by name afterwards.

## 3. Scene queries (read-only, no Full mode needed)

- `coreai_world_count_objects()`, `coreai_world_find(name)`, `coreai_world_get_transform(name)`
  (availability depends on host wiring - if a query global is nil, fall back to unity_* under Full).

## 4. Full tier (only when the host enabled Full Lua)

Inspect first, edit second. Ids come from the find functions.
- `unity_list_objects(max)`, `unity_find(name)`, `unity_find_all(pattern,max)`,
  `unity_find_by_tag(tag,max)`, `unity_find_by_component(type,max)`
- `unity_describe_object(id)`, `unity_get_transform(id)`, `unity_get_children(id)`,
  `unity_list_components(id)`
- `unity_set_position(id,x,y,z)`, `unity_set_rotation_euler(id,x,y,z)`, `unity_set_scale(id,x,y,z)`,
  `unity_parent(childId, parentIdOr0, worldPositionStays)`
- `unity_get_member(id,component,member)`, `unity_set_member(id,component,member,value)`,
  `unity_call(id,component,method,argsTable)`
Prefer coreai_world_* for plain transforms even in Full mode - it is cheaper and logged.

## 5. Timers & game loops (inside mods)

- `hooks_every(seconds, fn)` - repeating timer, minimum interval 0.05 s.
- `hooks_on('tick', fn)` - per-frame alias (about 20 Hz budget; also 'update'/'frame').
- There is no per-frame dt argument; accumulate your own counters.
- Pattern - variable speed without re-registering timers: run a fixed 0.1 s timer and use an
  accumulator (`acc = acc + (fast and 5 or 1); if acc >= 5 then acc = 0; step() end`).
- Frame-rate tolerance: a background WebGL tab can run at a few fps; never assume a timer
  fired an exact number of times - count what actually happened.

## 6. Persistence (per-mod store)

- `store_set(key, value)` / `store_get(key)` -> string ('' when missing). Strings only:
  `store_set('score', tostring(score))`; `score = tonumber(store_get('score')) or 0`.
- Values survive reload and app restart. The store is private to the mod.

## 7. Events & cross-mod communication

Events (fire-and-forget broadcast):
- `events_emit(name, payload)` - payload is a string; other mods AND the host can listen.
- `hooks_on(name, function(evt, payload) ... end)` - receive. Dispatch happens on the next tick.

Exports (shared variables and functions, pull-based):
- `mods_export(name, valueOrFunction)` - publish from the providing mod (limit 64 per mod).
- `mods_get(otherModId, name)` - read an exported VALUE. Copies by value (nil/boolean/number/
  string/plain tables, nesting up to 4): mutating the copy never affects the provider.
- `mods_call(otherModId, fnName, ...)` - call an exported FUNCTION; args/results copy by value;
  provider keeps its own state between calls. Cross-call depth is capped at 8 (cycles error out).
- `mods_list_exports(otherModId)` -> table of names.
- `mods_get` on a function errors ('use mods_call'); `mods_call` on a value errors too.
- Load order matters: a mod that calls `mods_get` at load time requires the provider to be
  loaded FIRST. Prefer reading inside handlers/timers, which run after everything loaded.
- Choose events for notifications (something happened), exports for queries (give me a value).

## 8. Player input (Gameplay tier)

- `input_key('a')` held; `input_key_down('space')` pressed this frame; `input_key_up` released.
  Key names are case-insensitive KeyCode spellings ('a','space','return','leftarrow') plus
  aliases 'left','right','up','down' and digits '0'..'9'.
- `input_mouse_button(0|1|2)`, `input_mouse_down(i)`, `input_mouse_x()`, `input_mouse_y()`.
- `input_axis('Horizontal')` -> number, 0 for undefined axes.
- Edge checks (`_down`/`_up`) are true for one frame only - a 20 Hz timer can miss them.
  Poll HELD state from timers and derive your own edges:
  `local now = input_key('w'); if now and not was then rotate() end; was = now`.

## 9. Logic slots (when the host defines rule slots)

- `logic_list()` -> defined slots; `logic_define('slot', function(...) return value end)`;
  `logic_reset('slot')`. Only touch slots that logic_list actually returns.

## 10. Output

- `report(msg)` - the intended channel; muted by default in mod managers, host enables per mod.
- `print(msg)` - routed into the same report pipeline inside mods; fine for quick debugging.
- Do not spam from timers; report state changes, not every tick.

## 11. Complete mod example - miniature falling-block game

Shows: spawn, color, per-load unique names, input with edge detection, gravity accumulator,
store persistence, an event another mod (or the host) can send.

```lua
-- Per-load generation suffix: Unity destroys deferred (end of frame), so a reload that
-- destroys the old objects and spawns identical names in the same frame would collide.
local gen = (tonumber(store_get('gen')) or 0) + 1
store_set('gen', tostring(gen))
local ROOT = 'MiniGameRoot_g' .. gen
local prev = 'MiniGameRoot_g' .. (gen - 1)
if coreai_world_exists(prev) then coreai_world_destroy(prev) end
coreai_world_spawn({ prefab = 'empty', name = ROOT, x = 0, y = 0, z = 0 })

local score = tonumber(store_get('score')) or 0
local px, py = 0, 10
coreai_world_spawn({ prefab = 'cube', name = ROOT .. '_p', parent = ROOT, x = px, y = py, z = 0 })
coreai_world_set_color(ROOT .. '_p', '#00CCFF')

local was_w = false
hooks_every(0.05, function()                      -- input at 20 Hz
  if input_key('a') and px > -4 then px = px - 1 end
  if input_key('d') and px <  4 then px = px + 1 end
  local w = input_key('w')                        -- derived edge, never input_key_down here
  if w and not was_w then score = score + 1 end
  was_w = w
  coreai_world_change(ROOT .. '_p', { x = px, y = py, z = 0 })
end)

local acc = 0
hooks_every(0.1, function()                       -- gravity with accumulator
  acc = acc + (input_key('s') and 5 or 1)
  if acc < 5 then return end
  acc = 0
  py = py - 1
  if py <= 0 then
    py = 10
    score = score + 10
    store_set('score', tostring(score))
    report('landed, score=' .. score)
  end
end)

hooks_on('minigame_reset', function(evt, payload) -- external control via events_emit
  score = 0
  store_set('score', '0')
end)
```

## 12. Common errors and their causes

- ""')' expected (to close '(' at line 1)"" right after load: the code string was mangled in
  the tool call - usually double-escaped newlines (\\n). Use plain \n in the JSON string.
- ""Mod 'x' is already loaded"": use action=reload, not load.
- ""mods_get: mod 'y' is not loaded"": load-order problem - read inside a handler instead of
  the load chunk, or load the provider first.
- ""cross-mod call depth limit reached"": two mods call each other in a cycle - break it.
- Values shared via mods_get are COPIES - writing to them does nothing visible to others.
- ""Lua exceeded 100 ms"": too much work in one handler - spread it over ticks.
- hooks_every interval below 0.05 s is clamped - do not rely on faster timers.
- report() shows nothing: report logging is disabled for the mod by default - it is a host
  switch, not an error; state still changes.
- A spawn you cannot see: wrong prefab key - call coreai_world_list_prefabs() first;
  primitives 'cube'/'sphere'/'cylinder'/'capsule'/'plane'/'empty' always work.";
    }
}