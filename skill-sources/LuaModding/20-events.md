# Events and calls between mods, logic slots, output

## Events & cross-mod communication

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

## Logic slots (when the host defines rule slots)

- `logic_list()` -> defined slots; `logic_define('slot', function(...) return value end)`;
  `logic_reset('slot')`. Only touch slots that logic_list actually returns.

## Output

- `report(msg)` - the intended channel; muted by default in mod managers, host enables per mod.
- `print(msg)` - routed into the same report pipeline inside mods; fine for quick debugging.
- Do not spam from timers; report state changes, not every tick.
