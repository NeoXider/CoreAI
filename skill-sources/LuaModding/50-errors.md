# Common errors and their causes

- "')' expected (to close '(' at line 1)" right after load: the code string was mangled in
  the tool call - usually double-escaped newlines (\\n). Use plain \n in the JSON string.
- "Mod 'x' is already loaded": use action=edit (or reload with the whole source), not load.
- "edit: old_text was not found": copy old_text again from get_source data, spaces and
  indentation included, starting at the from_line the message names.
- "mods_get: mod 'y' is not loaded": load-order problem - read inside a handler instead of
  the load chunk, or load the provider first.
- "cross-mod call depth limit reached": two mods call each other in a cycle - break it.
- Values shared via mods_get are COPIES - writing to them does nothing visible to others.
- "Lua exceeded 100 ms": too much work in one handler - spread it over ticks.
- hooks_every interval below 0.05 s is clamped - do not rely on faster timers.
- report() shows nothing: report logging is disabled for the mod by default - it is a host
  switch, not an error; state still changes.
- A Part you cannot see: set .Parent (usually workspace) after configuring it and set
  Anchored = true; remember coordinates are in STUDS (1 stud = 0.28 m), so meter-sized
  numbers build a tiny scene.
