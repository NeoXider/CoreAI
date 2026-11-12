# Timers and game loops, per-mod persistence, player input

## Timers & game loops (inside mods)

- `hooks_every(seconds, fn)` - repeating timer, minimum interval 0.05 s.
- `hooks_on('tick', fn)` - per-frame alias (about 20 Hz budget; also 'update'/'frame').
- There is no per-frame dt argument; accumulate your own counters.
- Pattern - variable speed without re-registering timers: run a fixed 0.1 s timer and use an
  accumulator (`acc = acc + (fast and 5 or 1); if acc >= 5 then acc = 0; step() end`).
- Frame-rate tolerance: a background WebGL tab can run at a few fps; never assume a timer
  fired an exact number of times - count what actually happened.

## Persistence (per-mod store)

- `store_set(key, value)` / `store_get(key)` -> string ('' when missing). Strings only:
  `store_set('score', tostring(score))`; `score = tonumber(store_get('score')) or 0`.
- Values survive reload and app restart. The store is private to the mod.

## Player input (Gameplay tier)

- `input_key('a')` held; `input_key_down('space')` pressed this frame; `input_key_up` released.
  Key names are case-insensitive KeyCode spellings ('a','space','return','leftarrow') plus
  aliases 'left','right','up','down' and digits '0'..'9'.
- `input_mouse_button(0|1|2)`, `input_mouse_down(i)`, `input_mouse_x()`, `input_mouse_y()`.
- `input_axis('Horizontal')` -> number, 0 for undefined axes.
- Edge checks (`_down`/`_up`) are true for one frame only - a 20 Hz timer can miss them.
  Poll HELD state from timers and derive your own edges:
  `local now = input_key('w'); if now and not was then rotate() end; was = now`.
